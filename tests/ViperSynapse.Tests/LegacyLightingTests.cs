using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class LegacyLightingTests
{
    [Theory]
    [InlineData(EffectKind.Static, "06A123BC")]
    [InlineData(EffectKind.Spectrum, "04")]
    [InlineData(EffectKind.Off, "00")]
    public void BlackWidow_sends_standard_keyboard_effect_and_backlight_brightness(EffectKind effect, string arguments)
    {
        var firmware = new LightingFirmware();
        using var control = Open(firmware, 0x0221);
        var profile = ProfileWith(("main", new ZoneLighting(new ZoneEffect(effect, new Rgb(0xA1, 0x23, 0xBC), default), 50)));

        control.Apply(profile);

        Assert.Null(control.LastProblem);
        Assert.Collection(firmware.Requests,
            report => AssertReport(report, 0x3F, 0x0A, Convert.FromHexString(arguments)),
            report => AssertReport(report, 0xFF, 0x03, 0x01, 0x05, 128),
            report => AssertReport(report, 0xFF, 0x83, 0x01, 0x05, 0));
        Assert.Equal<byte>(128, firmware.Brightness[0x05]);
        firmware.Requests.Clear();
        control.Apply(profile);
        Assert.Empty(firmware.Requests);
    }

    [Fact]
    public void Naga_uses_three_distinct_leds_and_a_separate_brightness_transaction()
    {
        var firmware = new LightingFirmware();
        using var control = Open(firmware, 0x0053);
        var profile = ProfileWith(
            ("backlight", new ZoneLighting(new ZoneEffect(EffectKind.Static, new Rgb(0x12, 0x34, 0x56), default), 20)),
            ("logo", new ZoneLighting(new ZoneEffect(EffectKind.Spectrum, default, default), 60)),
            ("scroll", new ZoneLighting(new ZoneEffect(EffectKind.Off, default, default), 80)));

        control.Apply(profile);

        Assert.Null(control.LastProblem);
        Assert.Equal(9, firmware.Requests.Count);
        AssertReport(ForLed(firmware, 0x0D, 0x05), 0x3F, 0x0D, 0x01, 0x05, 0x06, 0x12, 0x34, 0x56);
        AssertReport(ForLed(firmware, 0x0D, 0x04), 0x3F, 0x0D, 0x01, 0x04, 0x04);
        AssertReport(ForLed(firmware, 0x0D, 0x01), 0x3F, 0x0D, 0x01, 0x01, 0x00);
        AssertBrightness(firmware, 0x05, 51);
        AssertBrightness(firmware, 0x04, 153);
        AssertBrightness(firmware, 0x01, 204);
    }

    [Theory]
    [InlineData(0x0221, "main", 0x05)]
    [InlineData(0x0053, "logo", 0x04)]
    public void Idle_dimming_and_wake_only_change_brightness(ushort productId, string zone, byte led)
    {
        var firmware = new LightingFirmware();
        using var control = Open(firmware, productId);
        control.Apply(ProfileWith((zone, new ZoneLighting(ZoneEffect.Default, 40))));

        firmware.Requests.Clear();
        control.SetDimmed(true);
        Assert.Equal(2, firmware.Requests.Count);
        AssertBrightness(firmware, led, 0);

        firmware.Requests.Clear();
        control.SetDimmed(false);
        Assert.Equal(2, firmware.Requests.Count);
        AssertBrightness(firmware, led, 102);
        Assert.Null(control.LastProblem);
    }

    [Fact]
    public void A_mismatched_brightness_readback_can_retry_the_same_profile()
    {
        var firmware = new LightingFirmware { IgnoreBrightnessWrites = true };
        using var control = Open(firmware, 0x0053);
        var profile = ProfileWith(("scroll", new ZoneLighting(ZoneEffect.Default, 100)));

        control.Apply(profile);
        Assert.NotNull(control.LastProblem);

        firmware.IgnoreBrightnessWrites = false;
        firmware.Requests.Clear();
        control.Apply(profile);
        Assert.Null(control.LastProblem);
        AssertBrightness(firmware, 0x01, 255);
    }

    [Theory]
    [InlineData(0x0221, "main", 6, 22)]
    [InlineData(0x0053, "scroll", 1, 3)]
    public void Custom_frame_uploads_every_row_before_activating_and_restores_after_idle(
        ushort productId, string zone, int rows, int columns)
    {
        var firmware = new LightingFirmware();
        using var control = Open(firmware, productId);
        var frame = new CustomFrame(rows, columns, Enumerable.Range(0, rows * columns)
            .Select(i => new Rgb((byte)(i + 1), (byte)(255 - i), (byte)(i * 17))).ToArray());
        var profile = FrameProfile(zone, frame);

        control.Apply(profile);
        Assert.Null(control.LastProblem);
        AssertFrame(firmware, productId, frame);

        firmware.Requests.Clear();
        control.SetDimmed(true);
        Assert.Null(control.LastProblem);
        AssertFrame(firmware, productId, CustomFrame.Filled(rows, columns, default));

        firmware.Requests.Clear();
        control.SetDimmed(false);
        Assert.Null(control.LastProblem);
        AssertFrame(firmware, productId, frame);
    }

    [Theory]
    [InlineData(0x0221, "main", 6, 22, 2)]
    [InlineData(0x0053, "scroll", 1, 3, 0)]
    public void A_rejected_row_never_activates_the_partial_frame_and_the_same_profile_can_retry(
        ushort productId, string zone, int rows, int columns, int rejectedRow)
    {
        var firmware = new LightingFirmware { RejectFrameRow = rejectedRow };
        using var control = Open(firmware, productId);
        var frame = CustomFrame.Filled(rows, columns, new Rgb(1, 2, 3));
        var profile = FrameProfile(zone, frame);

        control.Apply(profile);
        Assert.NotNull(control.LastProblem);
        Assert.Equal(rejectedRow + 1, FrameReports(firmware, productId).Count);
        Assert.All(FrameReports(firmware, productId), report => Assert.Equal(
            productId == 0x0221 ? (byte)0x0B : (byte)0x03, report.CommandId));

        firmware.Requests.Clear();
        firmware.RejectFrameRow = null;
        control.Apply(profile);
        Assert.Null(control.LastProblem);
        AssertFrame(firmware, productId, frame);
    }

    [Theory]
    [InlineData(0x0221, 3, 2, "A")]
    [InlineData(0x0053, 0, 2, "Thumb Grid")]
    public Task Painting_in_the_advanced_editor_saves_a_frame_and_applies_it_to_emulated_firmware(
        ushort productId, int row, int column, string label) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var firmware = new LightingFirmware();
            using var control = Open(firmware, productId);
            var saved = new TaskCompletionSource<LightingSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var editor = new LightingViewModel(DeviceCatalogue.Default.Find(productId), lighting =>
            {
                control.Apply(Profile.Create("Edited frame") with { Lighting = lighting });
                saved.TrySetResult(lighting);
            });
            editor.Load(null);
            editor.SyncAll = false;
            editor.EffectTab = LightingViewModel.EffectTabs[1];
            Assert.True(editor.ShowFrameEditor);
            var cell = Assert.Single(editor.Cells, cell => cell.Row == row && cell.Column == column);
            Assert.Equal(label, cell.Label);
            if (productId == 0x0053)
                Assert.Equal(new[] { "Scroll Wheel", "Logo", "Thumb Grid" }, editor.Cells.Select(led => led.Label));
            editor.PaintColor = Color.FromRgb(0x12, 0x34, 0x56);
            editor.PaintCell(cell);

            var lighting = await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var frame = Assert.IsType<CustomFrame>(lighting.Frame);
            Assert.Equal(new Rgb(0x12, 0x34, 0x56), frame[row, column]);
            Assert.All(editor.Zones, zone => Assert.Equal(EffectKind.Custom, lighting.Zones[zone].Effect.Kind));
            AssertFrame(firmware, productId, frame);
            Assert.Null(control.LastProblem);
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData("0221", "keyboard", "A")]
    [InlineData("0053", "mouse", "Thumb Grid")]
    public Task The_lighting_page_paints_a_labelled_key_or_led(string productId, string type, string label) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var info = new ViperSynapse.App.Services.DeviceInfo(productId, productId, "Legacy device", type, "usb-legacy-test", true, true,
                null, null, null, [], false, null);
            var device = new DeviceViewModel(info, main) { SelectedPage = "LIGHTING" };
            device.Lighting.Load(null);
            var view = new DeviceView { DataContext = device };
            var window = new Window { Width = 1450, Height = 1000, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var effects = Assert.IsType<ListBox>(view.FindControl<ListBox>("FrameEffectTabs"));
                effects.SelectedItem = LightingViewModel.EffectTabs[1];
                var grid = Assert.IsType<ItemsControl>(view.FindControl<ItemsControl>("LedPaintGrid"));
                IEnumerable<Button> PaintButtons() => grid.GetVisualDescendants().OfType<Button>()
                    .Where(control => control.Content as string == label);
                for (int attempt = 0; attempt < 50 && !PaintButtons().Any(); attempt++)
                    await Task.Delay(20);
                Assert.Equal(LightingViewModel.EffectTabs[1], device.Lighting.EffectTab);
                Assert.True(grid.IsEffectivelyVisible);
                var button = Assert.Single(PaintButtons());
                var cell = Assert.IsType<LedCell>(button.DataContext);
                var paint = Color.FromRgb(0xDB, 0x42, 0xA1);
                device.Lighting.PaintColor = paint;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var edited = Assert.Single(device.Lighting.Cells, led => led.Row == cell.Row && led.Column == cell.Column);
                Assert.Equal(paint, Assert.IsType<SolidColorBrush>(edited.Brush).Color);
                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Theory]
    [InlineData(0x0221, "main", 6, 22)]
    [InlineData(0x0053, "scroll", 1, 3)]
    public void Malformed_frames_do_not_write_to_the_device(ushort productId, string zone, int rows, int columns)
    {
        var firmware = new LightingFirmware();
        using var control = Open(firmware, productId);
        control.Apply(FrameProfile(zone, new CustomFrame(rows, columns, [])));
        Assert.NotNull(control.LastProblem);
        control.Apply(FrameProfile(zone, new CustomFrame(rows, columns, null!)));
        Assert.NotNull(control.LastProblem);
        Assert.Empty(firmware.Requests);
    }

    [Theory]
    [InlineData(0x0221, "main")]
    [InlineData(0x0053, "backlight,logo,scroll")]
    public Task The_lighting_editor_offers_supported_presets_and_brightness(ushort productId, string zones) =>
        HeadlessApp.Session.Dispatch(() =>
        {
            var editor = new LightingViewModel(DeviceCatalogue.Default.Find(productId), _ => { });
            editor.Load(null);

            Assert.Equal(zones.Split(','), editor.Zones);
            foreach (string zone in editor.Zones)
            {
                editor.SelectedZone = zone;
                Assert.Equal(
                    new[] { EffectKind.Static, EffectKind.Spectrum, EffectKind.Off },
                    editor.QuickEffects.Select(effect => effect.Kind));
                Assert.All(editor.QuickEffects, effect => Assert.Equal(ColorMode.Single, Assert.Single(effect.Modes)));
                Assert.True(editor.ShowBrightness);
                Assert.True(editor.HasFrame);
            }
        }, CancellationToken.None);

    static Profile ProfileWith(params (string Zone, ZoneLighting Lighting)[] zones) =>
        Profile.Create("Legacy lighting") with { Lighting = new LightingSettings(zones.ToDictionary(z => z.Zone, z => z.Lighting)) };

    static Profile FrameProfile(string zone, CustomFrame frame) => Profile.Create("Legacy frame") with
    {
        Lighting = new LightingSettings(new Dictionary<string, ZoneLighting>
        {
            [zone] = new(new ZoneEffect(EffectKind.Custom, default, default))
        }, frame)
    };

    static List<RazerReport> FrameReports(LightingFirmware firmware, ushort productId) =>
        firmware.Requests.Where(report => productId == 0x0221
            ? report.CommandClass == 0x03 && report.CommandId is 0x0B or 0x0A
            : report.CommandClass == 0x0F && report.CommandId is 0x03 or 0x02).ToList();

    static void AssertFrame(LightingFirmware firmware, ushort productId, CustomFrame frame)
    {
        bool keyboard = productId == 0x0221;
        byte commandClass = keyboard ? (byte)0x03 : (byte)0x0F;
        var reports = FrameReports(firmware, productId);
        Assert.Equal(frame.Rows + 1, reports.Count);
        for (int row = 0; row < frame.Rows; row++)
        {
            var report = reports[row];
            Assert.Equal<byte>(0x3F, report.TransactionId);
            Assert.Equal(commandClass, report.CommandClass);
            Assert.Equal(keyboard ? (byte)0x0B : (byte)0x03, report.CommandId);
            Assert.Equal(keyboard ? (byte)70 : (byte)71, report.DataSize);
            byte[] expected = new byte[report.DataSize];
            int offset = keyboard ? 4 : 5;
            expected[0] = keyboard ? (byte)0xFF : (byte)0;
            expected[offset - 3] = (byte)row;
            expected[offset - 1] = (byte)(frame.Columns - 1);
            for (int column = 0; column < frame.Columns; column++)
            {
                var rgb = frame[row, column];
                expected[offset++] = rgb.R;
                expected[offset++] = rgb.G;
                expected[offset++] = rgb.B;
            }
            Assert.Equal(expected, report.Arguments[..report.DataSize].ToArray());
        }
        var activate = reports[^1];
        Assert.Equal<byte>(0x3F, activate.TransactionId);
        Assert.Equal(commandClass, activate.CommandClass);
        Assert.Equal(keyboard ? (byte)0x0A : (byte)0x02, activate.CommandId);
        byte[] activation = keyboard ? [0x05, 0x00] : [0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(activation, activate.Arguments[..activate.DataSize].ToArray());
    }

    static DeviceControl Open(LightingFirmware firmware, ushort productId) =>
        new(new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero }, 0x1F, DeviceCatalogue.Default.Find(productId));

    static RazerReport ForLed(LightingFirmware firmware, byte commandId, byte led) =>
        Assert.Single(firmware.Requests, report => report.CommandId == commandId && report.Arguments[1] == led);

    static void AssertBrightness(LightingFirmware firmware, byte led, byte brightness)
    {
        AssertReport(ForLed(firmware, 0x03, led), 0xFF, 0x03, 0x01, led, brightness);
        AssertReport(ForLed(firmware, 0x83, led), 0xFF, 0x83, 0x01, led, 0);
        Assert.Equal(brightness, firmware.Brightness[led]);
    }

    static void AssertReport(RazerReport report, byte transactionId, byte commandId, params byte[] arguments)
    {
        Assert.Equal(transactionId, report.TransactionId);
        Assert.Equal<byte>(0x03, report.CommandClass);
        Assert.Equal(commandId, report.CommandId);
        Assert.Equal(arguments, report.Arguments[..report.DataSize].ToArray());
    }

    sealed class LightingFirmware : IHidFeatureDevice
    {
        RazerReport? _pending;
        public string Path => "/emulated/legacy-lighting";
        public bool IgnoreBrightnessWrites { get; set; }
        public int? RejectFrameRow { get; set; }
        public Dictionary<byte, byte> Brightness { get; } = [];
        public List<RazerReport> Requests { get; } = [];

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            Assert.Equal<byte>(0, report[0]);
            var request = RazerReport.Parse(report[1..]);
            Assert.True(request.ChecksumValid);
            Requests.Add(request);
            _pending = request;
        }

        public int ReceiveFeature(Span<byte> report)
        {
            var request = Assert.IsType<RazerReport>(_pending);
            _pending = null;
            byte[] arguments = request.Arguments[..request.DataSize].ToArray();
            if (request.CommandClass == 0x03)
            {
                if (request.CommandId == 0x03 && !IgnoreBrightnessWrites)
                    Brightness[arguments[1]] = arguments[2];
                if (request.CommandId == 0x83)
                    arguments[2] = Brightness.GetValueOrDefault(arguments[1]);
            }
            RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, arguments).Bytes.CopyTo(report[1..]);
            int? frameRow = (request.CommandClass, request.CommandId) switch
            {
                (0x03, 0x0B) => arguments[1],
                (0x0F, 0x03) => arguments[2],
                _ => null
            };
            report[1] = (byte)(frameRow.HasValue && frameRow == RejectFrameRow ? ReportStatus.Failure : ReportStatus.Successful);
            return report.Length;
        }

        public void Dispose() { }
    }
}
