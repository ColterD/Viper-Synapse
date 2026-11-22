using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

public sealed class CpuTemperatureTests
{
    [Fact]
    public void Fresh_reader_returns_cpu_temperature_on_first_read()
    {
        using var hwmon = new FakeHwmon();
        hwmon.AddSensor("k10temp", "Tctl", "52750");

        var temperature = new CpuTemperature(hwmon.Root).Read();

        Assert.Equal(52.75, temperature);
    }

    [Fact]
    public void Missing_sensor_returns_null()
    {
        using var hwmon = new FakeHwmon();

        Assert.Null(new CpuTemperature(hwmon.Root).Read());
    }

    [Fact]
    public void Malformed_sensor_input_returns_null()
    {
        using var hwmon = new FakeHwmon();
        hwmon.AddSensor("coretemp", "Package id 0", "not-a-temperature");

        Assert.Null(new CpuTemperature(hwmon.Root).Read());
    }

    sealed class FakeHwmon : IDisposable
    {
        public FakeHwmon()
        {
            Root = Path.Join(Path.GetTempPath(), $"viper-synapse-hwmon-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void AddSensor(string driver, string label, string input)
        {
            var sensor = Path.Join(Root, "hwmon0");
            Directory.CreateDirectory(sensor);
            File.WriteAllText(Path.Join(sensor, "name"), driver);
            File.WriteAllText(Path.Join(sensor, "temp1_label"), label);
            File.WriteAllText(Path.Join(sensor, "temp1_input"), input);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
