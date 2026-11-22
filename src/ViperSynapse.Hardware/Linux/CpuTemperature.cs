namespace ViperSynapse.Hardware.Linux;

/// <summary>
/// The CPU temperature from the kernel's hwmon sensors: AMD's k10temp (Tctl) or zenpower, Intel's coretemp (the
/// package), or an ARM board's cpu_thermal. Read at most once a second; null when there is no such sensor.
/// </summary>
public sealed class CpuTemperature
{
    static readonly string[] Drivers = ["k10temp", "zenpower", "coretemp", "cpu_thermal"];

    readonly Lock _gate = new();
    readonly string _root;
    readonly Func<long> _tickCount;
    string? _input;
    bool _searched;
    double? _last;
    bool _read;
    long _readAt;
    public CpuTemperature(string root = "/sys/class/hwmon") : this(root, static () => Environment.TickCount64) { }

    internal CpuTemperature(string root, Func<long> tickCount)
    {
        _root = root;
        _tickCount = tickCount;
    }

    /// <summary>Degrees Celsius, or null when no CPU sensor was found or it cannot be read.</summary>
    public double? Read()
    {
        lock (_gate)
        {
            long now = _tickCount();
            if (_read && now - _readAt < 1000)
                return _last;
            _read = true;
            _readAt = now;
            try
            {
                if (!_searched)
                {
                    var (input, complete) = Find();
                    if (complete || input != null)
                        _input = input;
                    _searched = complete;
                }
                _last = _input != null && int.TryParse(File.ReadAllText(_input).Trim(), out var milli) ? milli / 1000.0 : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _last = null;
            }
            return _last;
        }
    }

    (string? Input, bool Complete) Find()
    {
        bool complete = true;
        string ReadMetadata(string path)
        {
            try { return File.ReadAllText(path).Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                complete = false;
                return "";
            }
        }

        List<(string Dir, string Name)> sensors;
        try
        {
            sensors = Directory.EnumerateDirectories(_root)
                .Select(dir => (Dir: dir, Name: ReadMetadata(Path.Join(dir, "name"))))
                .Where(sensor => Drivers.Contains(sensor.Name))
                .OrderBy(sensor => Array.IndexOf(Drivers, sensor.Name))
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return (null, true);
        }

        foreach (var (dir, _) in sensors)
        {
            // The labelled package or control temperature when there is one, else the first input.
            string? labelled = null;
            try
            {
                labelled = Directory.EnumerateFiles(dir, "temp*_label")
                    .FirstOrDefault(label => ReadMetadata(label) is "Tctl" or "Tdie" or "Package id 0");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
            var input = labelled != null ? labelled.Replace("_label", "_input", StringComparison.Ordinal) : Path.Join(dir, "temp1_input");
            if (File.Exists(input))
                return (input, complete);
        }
        return (null, complete);
    }
}
