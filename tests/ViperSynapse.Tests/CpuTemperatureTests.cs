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

    [Fact]
    public void Unreadable_sensor_name_does_not_hide_valid_sensor_and_discovery_retries()
    {
        using var hwmon = new FakeHwmon();
        long now = 0;
        hwmon.AddUnreadableNameSensor("52750");
        hwmon.AddSensor("coretemp", "Package id 0", "41000", index: 1);
        var temperature = new CpuTemperature(hwmon.Root, () => now);

        Assert.Equal(41, temperature.Read());

        hwmon.MakeNameReadable("k10temp");
        now = 1000;
        Assert.Equal(52.75, temperature.Read());
    }

    [Fact]
    public void Unreadable_preferred_label_uses_fallback_and_discovery_retries()
    {
        using var hwmon = new FakeHwmon();
        long now = 0;
        hwmon.AddSensor("k10temp", "edge", "40000");
        hwmon.AddUnreadableLabel(2, "60000");
        var temperature = new CpuTemperature(hwmon.Root, () => now);

        Assert.Equal(40, temperature.Read());

        hwmon.MakeLabelReadable(2, "Tctl");
        now = 1000;
        Assert.Equal(60, temperature.Read());
    }

    sealed class FakeHwmon : IDisposable
    {
        public FakeHwmon()
        {
            Root = Path.Join(Path.GetTempPath(), $"viper-synapse-hwmon-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void AddSensor(string driver, string label, string input, int index = 0)
        {
            var sensor = Path.Join(Root, $"hwmon{index}");
            Directory.CreateDirectory(sensor);
            File.WriteAllText(Path.Join(sensor, "name"), driver);
            File.WriteAllText(Path.Join(sensor, "temp1_label"), label);
            File.WriteAllText(Path.Join(sensor, "temp1_input"), input);
        }

        public void AddUnreadableNameSensor(string input)
        {
            var sensor = Path.Join(Root, "hwmon0");
            Directory.CreateDirectory(Path.Join(sensor, "name"));
            File.WriteAllText(Path.Join(sensor, "temp1_input"), input);
        }

        public void MakeNameReadable(string driver)
        {
            var name = Path.Join(Root, "hwmon0", "name");
            Directory.Delete(name);
            File.WriteAllText(name, driver);
        }

        public void AddUnreadableLabel(int index, string input)
        {
            var sensor = Path.Join(Root, "hwmon0");
            File.CreateSymbolicLink(
                Path.Join(sensor, $"temp{index}_label"),
                Path.Join(sensor, $"missing-temp{index}-label"));
            File.WriteAllText(Path.Join(sensor, $"temp{index}_input"), input);
        }

        public void MakeLabelReadable(int index, string label)
        {
            var path = Path.Join(Root, "hwmon0", $"temp{index}_label");
            File.Delete(path);
            File.WriteAllText(path, label);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
