// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HidSharp;

namespace LibreHardwareMonitor.Hardware.Controller.AquaComputer;

internal sealed class QuadroTemperature
{
    public float Temperature { get; set; }
    public bool IsAvailble { get; set; }
}

internal sealed class QuadroFan
{
    public float Percentage { get; set; }
    public float Voltage { get; set; }
    public float Current { get; set; }
    public float Power { get; set; }
    public float Speed { get; set; }
    public float Torque { get; set; }
    public byte State { get; set; }
}

internal sealed class QuadroHidResult
{
    public IList<QuadroTemperature> Temperatures { get; set; }
    public float VCC { get; set; }
    public float Flow { get; set; }
    public IList<QuadroFan> Fans { get; set; }
    public DeviceInfo DeviceInfo { get; set; }
    public DeviceInfoExt deviceInfoExt { get; set; }
}

internal sealed class DeviceInfoExt
{
    public uint SystemState { get; set; }
    public byte Features { get; set; }
    public uint Time { get; set; }
    public uint PowerCycles { get; set; }
    public uint Runtime { get; set; }
}

internal sealed class DeviceInfo
{
    public byte Validator { get; set; } // Should be 1
    public ushort StructureId { get; set; }
    public uint Serial { get; set; }
    public ushort Hardware { get; set; }
    public ushort DeviceType { get; set; }
    public ushort Bootloader { get; set; }
    public ushort Firmware { get; set; } // Firmware version was 1032 at the time of writing
}

public static class BinaryReaderExtensionMethods
{
    public static ushort ReadUInt16BE(this BinaryReader reader)
    {
        return BitConverter.ToUInt16(reader.ReadBytesReversed(sizeof(ushort)), 0);
    }

    public static short ReadInt16BE(this BinaryReader reader)
    {
        return BitConverter.ToInt16(reader.ReadBytesReversed(sizeof(short)), 0);
    }

    public static uint ReadUInt32BE(this BinaryReader reader)
    {
        return BitConverter.ToUInt32(reader.ReadBytesReversed(sizeof(uint)), 0);
    }

    public static int ReadInt32BE(this BinaryReader reader)
    {
        return BitConverter.ToInt32(reader.ReadBytesReversed(sizeof(int)), 0);
    }

    public static byte[] ReadBytesReversed(this BinaryReader reader, int byteCount)
    {
        var result = reader.ReadBytes(byteCount);

        if (result.Length != byteCount)
            throw new Exception(string.Format("{0} bytes required from stream, but only {1} returned.", byteCount, result.Length));

        return result.Reverse().ToArray();
    }
}

internal sealed class Quadro : Hardware
{
    private const int CapabilityNumTemperatureSensors = 4;
    private const int CapabilityNumFans = 4;

    private readonly Sensor VCCSensor;
    private readonly Sensor FlowSensor;
    private readonly Sensor[] _rpmSensors = new Sensor[CapabilityNumFans];
    private readonly Sensor[] _temperatures = new Sensor[CapabilityNumTemperatureSensors];

    private readonly HidStream _stream;

    public Quadro(HidDevice dev, ISettings settings) : base("Quadro", new Identifier(dev.DevicePath), settings)
    {
        if (dev.TryOpen(out _stream))
        {
            var quadroReport = this.ReadQuadroData();

            Name = "Quadro";
            FirmwareVersion = quadroReport.DeviceInfo.Firmware;

            for (var i = 0; i < CapabilityNumTemperatureSensors; i++)
            {
                _temperatures[i] = new Sensor($"Temperature {i}", i, SensorType.Temperature, this, Array.Empty<ParameterDescription>(), settings);
                ActivateSensor(_temperatures[i]);
            }

            for (var i = 0; i < CapabilityNumFans; i++)
            {
                _rpmSensors[i] = new Sensor($"Fan {i}", i, SensorType.Fan, this, Array.Empty<ParameterDescription>(), settings);
                ActivateSensor(_rpmSensors[i]);
            }

            VCCSensor = new Sensor("VCC", 0, SensorType.Voltage, this, Array.Empty<ParameterDescription>(), settings);
            ActivateSensor(VCCSensor);

            FlowSensor = new Sensor("Flow", 0, SensorType.Flow, this, Array.Empty<ParameterDescription>(), settings);
            ActivateSensor(FlowSensor);
        }
    }

    public ushort FirmwareVersion { get; }

    public override HardwareType HardwareType
    {
        get { return HardwareType.Cooler; }
    }

    private QuadroHidResult ReadQuadroData()
    {
        var hidReportBytes = _stream.Read();

        var hidResult = new QuadroHidResult();
        var stream = new MemoryStream(hidReportBytes);
        var reader = new BinaryReader(stream);

        hidResult.DeviceInfo = new DeviceInfo()
        {
            Validator = reader.ReadByte(),
            StructureId = reader.ReadUInt16BE(),
            Serial = reader.ReadUInt32BE(),
            Hardware = reader.ReadUInt16BE(),
            DeviceType = reader.ReadUInt16BE(),
            Bootloader = reader.ReadUInt16BE(),
            Firmware = reader.ReadUInt16BE(),
        };

        hidResult.deviceInfoExt = new DeviceInfoExt()
        {
            SystemState = reader.ReadUInt32BE(),
            Features = reader.ReadByte(),
            Time = reader.ReadUInt32BE(),
            PowerCycles = reader.ReadUInt32BE(),
            Runtime = reader.ReadUInt32BE()
        };

        reader.ReadBytes(10 * sizeof(ushort)); // Unsupported data for now. Reading to progress the stream index.

        hidResult.Temperatures = new List<QuadroTemperature>();

        for (var i = 0; i < CapabilityNumTemperatureSensors; i++)
        {
            var temperature = reader.ReadUInt16BE();
            var isAvailble = temperature != short.MaxValue; // temperature == short.MaxValue => sensor unavailable

            hidResult.Temperatures.Add(new QuadroTemperature()
            {
                Temperature = temperature / 100.0f,
                IsAvailble = isAvailble,
            });
        }

        reader.ReadBytes(16 * sizeof(short)); // Unsupported data for now. Reading to progress the stream index. (Sensor stuff)
        reader.ReadBytes(16 * sizeof(byte)); // Unsupported data for now. Reading to progress the stream index. (Sensor stuff)

        hidResult.VCC = reader.ReadInt16BE() / 100.0f;
        hidResult.Flow = reader.ReadInt16BE() / 10.0f;

        hidResult.Fans = new List<QuadroFan>();

        for (var i = 0; i < 4; i++)
        {
            var fan = new QuadroFan()
            {
                Percentage = reader.ReadInt16BE() / 100.0f,
                Voltage = reader.ReadInt16BE() / 100.0f,
                Current = reader.ReadInt16BE() / 1000.0f,
                // Maybe the same rule as with the temperature applies (short.MaxValue) => sensor unavailable. But I haven't validated this.
                Power = reader.ReadInt16BE() / 100.0f,
                Speed = reader.ReadInt16BE(),
                Torque = reader.ReadInt16BE(),
                State = reader.ReadByte(),
            };

            hidResult.Fans.Add(fan);
        }

        return hidResult;
    }

    public override void Close()
    {
        _stream.Close();
        base.Close();
    }

    public override void Update()
    {
        var quadroReport = ReadQuadroData();

        for (var i = 0; i < _temperatures.Length; i++)
        {
            if (quadroReport.Temperatures[i].IsAvailble)
            {
                _temperatures[i].Value = quadroReport.Temperatures[i].Temperature;
            }
            else
            {
                _temperatures[i].Value = null;
            }
        }

        for (var i = 0; i < _rpmSensors.Length; i++)
        {
            _rpmSensors[i].Value = quadroReport.Fans[i].Speed;
        }

        VCCSensor.Value = quadroReport.VCC;
        FlowSensor.Value = quadroReport.Flow;
    }
}
