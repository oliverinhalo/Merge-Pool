using System.Collections.ObjectModel;
using System.Globalization;
using MergePool.Ipc.Protocol;
using MergePool.Ui.Services;
using MergePool.Ui.ViewModels.Settings;

namespace MergePool.Ui.ViewModels;

/// <summary>
/// Every tunable the engine has, as things a person can reason about: how MergePool chooses a drive
/// for a new file, when it decides a drive has gone slow, how it measures drive speed in the first
/// place, and how hard the background rebalancer is allowed to work.
/// </summary>
/// <remarks>
/// <para>The everyday settings are the four that change what MergePool does rather than how it
/// decides it: where new files go, how much space to keep clear, whether to even the drives out, and
/// how fast it may do that. Everything else — the exponents, the sample counts, the smoothing
/// factors, the probe sizes — sits behind the Advanced toggle, because it is only worth touching
/// with a specific problem in mind, and a bad value there is felt rather than seen.</para>
/// <para>Nothing is sent until Save, so a half-moved slider never reaches the engine. What comes
/// back from a save is what the service actually stored — it clamps anything out of range — and
/// that is what is shown, so the window never claims a value the engine is not using.</para>
/// </remarks>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ServiceConnection _service;
    private readonly Func<CancellationToken> _token;

    private PlacementSettingsDto _settings = Defaults();
    private bool _loading;
    private bool _applying;
    private bool _hasChanges;
    private bool _showAdvanced;
    private string? _message;

    public SettingsViewModel(ServiceConnection service, Func<CancellationToken> token)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        SaveCommand = new AsyncCommand(SaveAsync, () => HasChanges);
        ResetCommand = new AsyncCommand(ResetAsync);

        Groups = BuildGroups();
        ApplyAdvanced();
    }

    public AsyncCommand SaveCommand { get; }

    /// <summary>Puts every setting back to the value MergePool ships with.</summary>
    public AsyncCommand ResetCommand { get; }

    /// <summary>The settings, in the order the window shows them.</summary>
    public ObservableCollection<SettingGroup> Groups { get; }

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>
    /// Whether the expert tunables are on screen. Set from the window's Advanced toggle, so one
    /// switch governs the whole window rather than each tab having its own.
    /// </summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (Set(ref _showAdvanced, value))
            {
                ApplyAdvanced();
                Raise(nameof(ScopeText));
            }
        }
    }

    public string ScopeText => _showAdvanced
        ? string.Create(CultureInfo.CurrentCulture, $"Showing all {TotalCount} settings, expert ones included.")
          + " Each one says what it does and what it ships with, and the service refuses a value that"
          + " would stop it working."
        : "Showing the settings worth changing day to day. Turn on Advanced, in the title bar, for"
          + " every tunable the engine has.";

    private int TotalCount => Groups.Sum(group => group.Items.Count);

    /// <summary>Whether anything has been moved since the last save.</summary>
    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            if (Set(ref _hasChanges, value))
            {
                Raise(nameof(ChangeText));
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ChangeText
    {
        get
        {
            if (!_hasChanges)
            {
                var changed = Groups.Sum(group => group.ChangedCount);

                return changed == 0
                    ? "Saved. Every setting is at its default."
                    : string.Create(
                        CultureInfo.CurrentCulture,
                        $"Saved. {changed} of {TotalCount} settings differ from their defaults.");
            }

            return "Not saved yet — click Save settings to send these to the service.";
        }
    }

    public async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var settings = await _service
                .TryInvokeAsync<PlacementSettingsDto>(Methods.ConfigGet, null, _token())
                .ConfigureAwait(true);

            if (settings is not null)
            {
                // Mid-edit: the poll that refreshes the rest of the window must not pull a slider
                // out from under the person moving it.
                if (!HasChanges)
                {
                    _settings = settings;
                    Show();
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SaveAsync()
    {
        var stored = await _service
            .TryInvokeAsync<PlacementSettingsDto>(Methods.ConfigSetPlacement, _settings, _token())
            .ConfigureAwait(true);

        if (stored is null)
        {
            Message = _service.LastError ?? "The settings could not be saved.";
            return;
        }

        // What the service stored, not what was sent: it brings anything out of range back in, and
        // the window should show the value in force rather than the one that was asked for.
        var adjusted = Differs(_settings, stored);

        _settings = stored;
        Show();
        HasChanges = false;

        Message = adjusted
            ? "Saved. The service brought a value or two into the range it can work with, and the "
              + "sliders now show what it is actually using. The change applies to the next file "
              + "placed; nothing already in the pool moves."
            : "Saved. The change applies to the next file placed; nothing already in the pool moves.";
    }

    private async Task ResetAsync()
    {
        _settings = Defaults();
        Show();
        await SaveAsync().ConfigureAwait(true);
        Message = "Back to the defaults — every setting on this tab, advanced ones included.";
    }

    /// <summary>Pushes the current settings into the sliders without that counting as an edit.</summary>
    private void Show()
    {
        _applying = true;
        try
        {
            foreach (var item in Groups.SelectMany(group => group.Items))
            {
                switch (item)
                {
                    case NumericSetting numeric when _readers.TryGetValue(numeric, out var read):
                        numeric.Load(read(_settings));
                        break;

                    case ToggleSetting toggle when _switches.TryGetValue(toggle, out var read):
                        toggle.Load(read(_settings));
                        break;
                }
            }
        }
        finally
        {
            _applying = false;
        }

        RefreshGroups();
    }

    private void Changed()
    {
        if (_applying)
        {
            return;
        }

        HasChanges = true;
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        foreach (var group in Groups)
        {
            group.Refresh();
        }

        Raise(nameof(ChangeText));
    }

    private void ApplyAdvanced()
    {
        foreach (var group in Groups)
        {
            group.ApplyAdvanced(_showAdvanced);
        }
    }

    /// <summary>Where each slider reads its value from, so loading does not need a switch statement.</summary>
    private readonly Dictionary<NumericSetting, Func<PlacementSettingsDto, double>> _readers = [];

    private readonly Dictionary<ToggleSetting, Func<PlacementSettingsDto, bool>> _switches = [];

    /// <summary>
    /// Held because moving the slider that parks a slow drive can have to push this one up with
    /// it: the speed a drive has to regain is always above the speed that parked it.
    /// </summary>
    private NumericSetting? _throttleExit;

    private NumericSetting Numeric(
        string title,
        string explanation,
        string effect,
        double minimum,
        double maximum,
        double step,
        double defaultValue,
        Func<double, string> format,
        Func<double, string> describe,
        Func<PlacementSettingsDto, double> read,
        Action<double> write,
        bool advanced = false)
    {
        var setting = new NumericSetting(
            title,
            explanation,
            effect,
            minimum,
            maximum,
            step,
            defaultValue,
            format,
            describe,
            value =>
            {
                write(value);
                Changed();
            },
            advanced);

        _readers[setting] = read;
        return setting;
    }

    private ToggleSetting Toggle(
        string title,
        string explanation,
        string effect,
        bool defaultValue,
        string whenOn,
        string whenOff,
        Func<PlacementSettingsDto, bool> read,
        Action<bool> write,
        bool advanced = false)
    {
        var setting = new ToggleSetting(
            title,
            explanation,
            effect,
            defaultValue,
            whenOn,
            whenOff,
            value =>
            {
                write(value);
                Changed();
            },
            advanced);

        _switches[setting] = read;
        return setting;
    }

    private ObservableCollection<SettingGroup> BuildGroups() =>
    [
        WhereNewFilesGo(),
        SlowDrives(),
        HowDrivesAreMeasured(),
        EveningOutTheDrives(),
    ];

    private SettingGroup WhereNewFilesGo() => new(
        "Where new files go",
        "Every file written to the pool lands whole on one drive. These decide which one.",
        advanced: false,
        Numeric(
            "Empty drives or fast drives",
            "MergePool scores every drive for each new file and writes it to the winner. This is how "
            + "much the drive's speed counts against how much room it has left.",
            "All the way left fills the emptiest drive whatever its speed, which keeps the drives "
            + "level. All the way right sends new files to the fastest drive until it is nearly "
            + "full, which is quicker to write but leaves the drives uneven.",
            minimum: 0,
            maximum: 3,
            step: 0.05,
            defaultValue: 1.0,
            BiasLabel,
            value => Bias(value) + ".",
            settings => settings.SpeedWeight ?? 1.0,
            value => _settings.SpeedWeight = Math.Round(value, 2)),
        Numeric(
            "Space kept free on every drive",
            "A drive with less than this much room left is passed over for new files. It is not a "
            + "quota — nothing is moved off a drive to reach it — just a line below which a drive "
            + "stops being offered new work.",
            "A few gigabytes keeps Windows, and the drive itself, out of the trouble a completely "
            + "full disk causes. Set it to zero and MergePool will fill a drive to the last byte.",
            minimum: 0,
            maximum: 200,
            step: 0.5,
            defaultValue: 2,
            SettingFormats.Gigabytes,
            value => value <= 0
                ? "Drives are filled to the last byte."
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"New files avoid a drive with less than {SettingFormats.Gigabytes(value)} free."),
            settings => (settings.MinFreeBytes ?? 0) / (double)(1024 * 1024 * 1024),
            value => _settings.MinFreeBytes = (long)Math.Round(value * 1024 * 1024 * 1024)),
        Numeric(
            "Room left beyond the file itself",
            "Headroom demanded on top of the size of the file being written. A drive that could "
            + "take the file but not this much more besides is passed over for it.",
            "Raising it keeps a little breathing room on every drive as files land. Setting it to "
            + "zero lets a write fill a drive exactly, which can leave it with nothing spare for "
            + "the file system's own bookkeeping.",
            minimum: 0,
            maximum: 8192,
            step: 64,
            defaultValue: 256,
            SettingFormats.Megabytes,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"A drive needs the file's size plus {SettingFormats.Megabytes(value)} to be chosen for it."),
            settings => (settings.WriteHeadroomBytes ?? 256L * 1024 * 1024) / (double)(1024 * 1024),
            value => _settings.WriteHeadroomBytes = (long)Math.Round(value * 1024 * 1024),
            advanced: true),
        Numeric(
            "How hard free space counts",
            "The exponent on free space in the placement score. One means a drive with twice the "
            + "room is twice as likely to be chosen; two means four times as likely.",
            "Above one the emptiest drive wins nearly every file, which levels the drives fast and "
            + "concentrates writes on one disk. At zero, free space stops counting altogether and "
            + "only speed decides — the slider at the top of this group.",
            minimum: 0,
            maximum: 3,
            step: 0.05,
            defaultValue: 1,
            SettingFormats.Factor,
            value => value switch
            {
                < 0.05 => "Free space is ignored; only speed picks the drive.",
                < 0.95 => "Free space counts, but less than in proportion.",
                < 1.05 => "A drive with twice the free space is twice as likely to be chosen.",
                _ => "The emptiest drive wins all but the most lopsided comparisons.",
            },
            settings => settings.FreeSpaceWeight ?? 1.0,
            value => _settings.FreeSpaceWeight = Math.Round(value, 2),
            advanced: true));

    private SettingGroup SlowDrives() => new(
        "Slow drives",
        "A drive that has suddenly got slower is parked: it keeps serving the files it holds, but no "
        + "new ones are sent to it until it recovers. Each drive is judged against its own normal "
        + "speed, so an archive disk is not treated as broken for being an archive disk.",
        advanced: false,
        Numeric(
            "How quickly a slow drive is parked",
            "How far below its own normal speed a drive has to fall before MergePool stops sending "
            + "it new files. This is the symptom of an SMR disk whose cache has filled, a USB "
            + "enclosure dropping to a slower mode, or a drive that is overheating.",
            "To the right, a drive is parked at the first sign of a slowdown — responsive, but a "
            + "brief stall can take a healthy drive out of rotation for a minute. To the left, only "
            + "a drive that has nearly stopped is parked.",
            minimum: 0.1,
            maximum: 0.9,
            step: 0.05,
            defaultValue: 1 - 0.45,
            value => SettingFormats.Percent(1 - value),
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"A drive is parked once it writes below {1 - value:P0} of its own normal speed."),
            settings => 1 - Math.Clamp(settings.ThrottleEnterRatio ?? 0.45, 0.1, 0.9),
            value =>
            {
                var enter = Math.Round(1 - value, 2);
                _settings.ThrottleEnterRatio = enter;

                // A drive has to come back at a higher speed than it was parked at, or one noisy
                // sample flaps it in and out of the pool. The service enforces this too; doing it
                // here as well means the slider below moves in front of you rather than jumping
                // the moment you save.
                var exit = Math.Max(_settings.ThrottleExitRatio ?? 0.75, enter + 0.05);

                if (exit != _settings.ThrottleExitRatio)
                {
                    _settings.ThrottleExitRatio = exit;
                    _throttleExit?.Load(exit);
                }
            }),
        _throttleExit = Numeric(
            "Speed it has to regain to come back",
            "A parked drive is used again once it writes at this much of its normal speed. It is "
            + "deliberately higher than the figure that parked it.",
            "The gap between the two is what stops a borderline drive flapping in and out of "
            + "rotation. Narrow it and a drive returns sooner but less convincingly; widen it and a "
            + "recovered drive sits out longer than it needs to.",
            minimum: 0.15,
            maximum: 0.99,
            step: 0.01,
            defaultValue: 0.75,
            SettingFormats.Percent,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"A parked drive is used again once it is back above {value:P0} of its normal speed."),
            settings => settings.ThrottleExitRatio ?? 0.75,
            value => _settings.ThrottleExitRatio = Math.Round(value, 2),
            advanced: true),
        Numeric(
            "Slow responses count too",
            "A drive is also parked when it takes this many times longer than usual to answer, even "
            + "if the megabytes a second still look respectable. A disk doing its own housekeeping "
            + "behaves exactly like this.",
            "Lower it and MergePool reacts to a stuttering drive sooner. Raise it and only a drive "
            + "that has become dramatically unresponsive is parked for latency.",
            minimum: 1.2,
            maximum: 20,
            step: 0.1,
            defaultValue: 3,
            SettingFormats.Multiple,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"Answering {value:N1}× slower than usual parks the drive, however fast it still writes."),
            settings => settings.LatencyThrottleRatio ?? 3.0,
            value => _settings.LatencyThrottleRatio = Math.Round(value, 2),
            advanced: true),
        Numeric(
            "Bad samples before parking a drive",
            "How many measurements in a row have to look bad before the drive is parked. One "
            + "unlucky write is not a slow drive.",
            "One reacts immediately and will sometimes park a drive that was only briefly busy. "
            + "Several is steadier, at the cost of sending a few more files to a drive that has "
            + "genuinely gone slow.",
            minimum: 1,
            maximum: 20,
            step: 1,
            defaultValue: 3,
            SettingFormats.Samples,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"{value:N0} bad measurements in a row park a drive."),
            settings => settings.ThrottleEnterSamples ?? 3,
            value => _settings.ThrottleEnterSamples = (int)Math.Round(value),
            advanced: true),
        Numeric(
            "Good samples before using it again",
            "How many measurements in a row have to look healthy before a parked drive is given new "
            + "files again.",
            "More means a drive has to prove itself for longer before it is trusted with new files; "
            + "fewer brings it back sooner on less evidence.",
            minimum: 1,
            maximum: 20,
            step: 1,
            defaultValue: 3,
            SettingFormats.Samples,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"{value:N0} healthy measurements in a row bring a drive back."),
            settings => settings.ThrottleExitSamples ?? 3,
            value => _settings.ThrottleExitSamples = (int)Math.Round(value),
            advanced: true),
        Numeric(
            "Shortest time parked",
            "Once parked, a drive stays parked for at least this long, however good it starts to "
            + "look. It gives a disk that is catching up with itself time to actually catch up.",
            "Shorter puts a recovering drive back to work sooner. Longer leaves a drive that "
            + "recovers in bursts — an SMR disk flushing its cache — properly out of the way.",
            minimum: 5,
            maximum: 1800,
            step: 5,
            defaultValue: 60,
            SettingFormats.Seconds,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"A parked drive sits out at least {SettingFormats.Seconds(value)} before it can return."),
            settings => settings.ThrottleCooldownSeconds ?? 60,
            value => _settings.ThrottleCooldownSeconds = Math.Round(value),
            advanced: true),
        Numeric(
            "Measurements before judging a drive at all",
            "A drive needs this many measurements before MergePool is willing to call it slow. "
            + "Until then it has no normal speed to be compared against.",
            "Lower it and a drive added mid-session is judged sooner, on thinner evidence. Raise it "
            + "and a new drive is left alone for longer.",
            minimum: 1,
            maximum: 50,
            step: 1,
            defaultValue: 5,
            SettingFormats.Samples,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"A drive is never called slow on fewer than {value:N0} measurements."),
            settings => settings.MinSamplesForThrottleDetection ?? 5,
            value => _settings.MinSamplesForThrottleDetection = (int)Math.Round(value),
            advanced: true));

    private SettingGroup HowDrivesAreMeasured() => new(
        "How drives are measured",
        "What MergePool means by a drive's speed. It watches real writes as they happen and, when a "
        + "drive has been idle, measures it with one small write of its own. These settings are "
        + "worth touching only to explain a placement decision you disagree with.",
        advanced: true,
        Numeric(
            "Speed counted as normal",
            "The throughput treated as a speed factor of 1.0. A drive measured at twice this is "
            + "scored as twice as fast; one at half of it, half as fast.",
            "It is a yardstick, not a limit: nothing is capped at this rate. Set it near what your "
            + "typical drive actually achieves and the speed figures in the pool read sensibly.",
            minimum: 10,
            maximum: 2000,
            step: 10,
            defaultValue: 120,
            SettingFormats.Rate,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"A drive writing {value:N0} MB/s counts as ordinary speed."),
            settings => (settings.ReferenceThroughputBytesPerSecond ?? 120d * 1024 * 1024) / (1024 * 1024),
            value => _settings.ReferenceThroughputBytesPerSecond = value * 1024 * 1024,
            advanced: true),
        Numeric(
            "How fast the speed figure reacts",
            "The weight given to the newest measurement when updating a drive's current speed. "
            + "Everything below it is history.",
            "High follows every burst and lull, so placement reacts quickly and twitchily. Low "
            + "smooths the figure out, so a drive's score reflects the last few minutes rather than "
            + "the last few seconds.",
            minimum: 0.01,
            maximum: 1,
            step: 0.01,
            defaultValue: 0.25,
            SettingFormats.Factor,
            value => value switch
            {
                < 0.1 => "The speed figure moves slowly and ignores short bursts.",
                < 0.5 => "The speed figure follows the last few measurements.",
                _ => "The speed figure follows the newest measurement closely.",
            },
            settings => settings.EwmaAlpha ?? 0.25,
            value => _settings.EwmaAlpha = Math.Round(value, 3),
            advanced: true),
        Numeric(
            "How fast normal speed drifts down",
            "A drive's baseline — what MergePool considers normal for it — is deliberately slow to "
            + "fall, so an hour of heavy writing does not redefine a healthy drive as a slow one.",
            "Raise it and the baseline follows a drive downhill, which stops it being parked but "
            + "also stops a real slowdown being noticed. Lower it and the baseline is near enough "
            + "fixed at the drive's best behaviour.",
            minimum: 0.001,
            maximum: 0.5,
            step: 0.001,
            defaultValue: 0.02,
            SettingFormats.Factor,
            value => value switch
            {
                < 0.01 => "Normal speed is close to fixed: a slowdown stays visible for a long time.",
                < 0.1 => "Normal speed drifts slowly, so a real slowdown is still noticed.",
                _ => "Normal speed follows the drive down, so sustained slowness becomes the new normal.",
            },
            settings => settings.BaselineAlpha ?? 0.02,
            value => _settings.BaselineAlpha = Math.Round(value, 4),
            advanced: true),
        Numeric(
            "How fast normal speed rises",
            "The same baseline, going the other way: how readily it accepts a measurement better "
            + "than itself. It rises faster than it falls on purpose.",
            "High means a drive that has just done something fast is immediately expected to keep "
            + "it up, and judged against that. Low means a drive earns its reputation slowly.",
            minimum: 0.01,
            maximum: 1,
            step: 0.01,
            defaultValue: 0.3,
            SettingFormats.Factor,
            value => value switch
            {
                < 0.15 => "A drive's best speed is learned gradually.",
                < 0.6 => "A faster measurement raises what is expected of the drive within a few samples.",
                _ => "One fast measurement becomes the drive's new normal almost at once.",
            },
            settings => settings.BaselineRiseAlpha ?? 0.3,
            value => _settings.BaselineRiseAlpha = Math.Round(value, 3),
            advanced: true),
        Numeric(
            "Slowest a drive can be scored",
            "A floor under the measured speed factor. However badly a drive performs, it never "
            + "scores below this, so it keeps a small share of new files rather than none.",
            "Zero lets a struggling drive be ignored completely while it is struggling. Raising it "
            + "keeps work flowing to a slow drive, which is what you want if it is slow but fine "
            + "and holds the content you are writing.",
            minimum: 0.001,
            maximum: 1,
            step: 0.001,
            defaultValue: 0.05,
            SettingFormats.Factor,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"The slowest drive still scores at least {value:0.###} against a normal drive's 1."),
            settings => settings.MinSpeedFactor ?? 0.05,
            value => _settings.MinSpeedFactor = Math.Round(value, 4),
            advanced: true),
        Numeric(
            "Fastest a drive can be scored",
            "A ceiling on the measured speed factor, so one very fast drive — or one lucky "
            + "measurement on a cached write — cannot take every new file.",
            "Lower it to spread writes more evenly across mixed hardware. Raise it to let a genuinely "
            + "much faster drive, an NVMe among spinning disks, take the share its speed suggests.",
            minimum: 1,
            maximum: 64,
            step: 0.5,
            defaultValue: 8,
            SettingFormats.Factor,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"The fastest drive is scored at most {value:0.#}× a normal drive."),
            settings => settings.MaxSpeedFactor ?? 8.0,
            value => _settings.MaxSpeedFactor = Math.Round(value, 2),
            advanced: true),
        Numeric(
            "Smallest write that says anything about speed",
            "Writes below this size are too small to measure throughput with — they finish inside "
            + "the drive's own cache. They still count towards how quickly it responds.",
            "Raising it means only substantial writes shape the speed figure, which is more "
            + "accurate but gathers evidence more slowly on a pool that mostly stores small files.",
            minimum: 4,
            maximum: 4096,
            step: 4,
            defaultValue: 64,
            SettingFormats.Kilobytes,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"Only writes of {SettingFormats.Kilobytes(value)} or more shape the speed figure."),
            settings => (settings.MinThroughputSampleBytes ?? 64 * 1024) / 1024d,
            value => _settings.MinThroughputSampleBytes = (long)Math.Round(value * 1024),
            advanced: true),
        Numeric(
            "How often an idle drive is measured",
            "A drive nobody is writing to tells you nothing about its speed. After this long idle, "
            + "MergePool writes one small file to it to find out.",
            "Shorter keeps the speed figures fresh on a pool that is mostly quiet. Longer leaves "
            + "idle disks alone — worth it if they spin down, since a probe wakes them up.",
            minimum: 10,
            maximum: 7200,
            step: 10,
            defaultValue: 120,
            SettingFormats.Seconds,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"An idle drive is measured again after {SettingFormats.Seconds(value)}."),
            settings => settings.IdleProbeIntervalSeconds ?? 120,
            value => _settings.IdleProbeIntervalSeconds = Math.Round(value),
            advanced: true),
        Numeric(
            "Size of that measurement",
            "How much the idle probe writes. It is written inside the pool's own folder and deleted "
            + "straight away.",
            "Bigger is a better measurement and more honest about a drive that is slow in a sustained "
            + "way. Smaller costs an SMR disk almost nothing, which is why the default is as small "
            + "as it is.",
            minimum: 64,
            maximum: 65536,
            step: 64,
            defaultValue: 1024,
            SettingFormats.Kilobytes,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"Each probe writes {SettingFormats.Kilobytes(value)} and deletes it again."),
            settings => (settings.IdleProbeBytes ?? (1 << 20)) / 1024d,
            value => _settings.IdleProbeBytes = (int)Math.Round(value * 1024),
            advanced: true));

    private SettingGroup EveningOutTheDrives() => new(
        "Evening out the drives",
        "Placement keeps new files off the fullest drives, but it cannot undo a pool that was "
        + "already lopsided. The rebalancer moves files between drives in the background to fix "
        + "that, at a rate it is not allowed to exceed, skipping anything in use.",
        advanced: false,
        Toggle(
            "Even out the drives in the background",
            "Whether MergePool moves files off the fullest drives on its own. Files are moved whole, "
            + "one drive to another, with the copy verified before the original is removed.",
            "On, the drives converge over hours or days without you doing anything. Off, nothing "
            + "moves unless you click Rebalance yourself — which is what you want while you are "
            + "copying a lot of data in, or if the drives are noisy and nearby.",
            defaultValue: true,
            "On: files move between drives in the background to level them up.",
            "Off: nothing is moved unless you start a rebalance yourself.",
            settings => settings.RebalanceEnabled ?? true,
            value => _settings.RebalanceEnabled = value),
        Numeric(
            "Fastest it may move files",
            "A ceiling on what the rebalancer reads and writes. It is not a target: when the drives "
            + "are level it moves nothing at all.",
            "Low keeps it invisible, and a lopsided pool takes days to even out. High levels the "
            + "pool quickly and is noticeable while it does — in disk noise, and in anything else "
            + "reading from those drives.",
            minimum: 1,
            maximum: 500,
            step: 1,
            defaultValue: 32,
            SettingFormats.Rate,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"Rebalancing moves at most {value:N0} MB/s, so it stays out of the way of real work."),
            settings => (settings.RebalanceMaxBytesPerSecond ?? 33_554_432) / (double)(1024 * 1024),
            value => _settings.RebalanceMaxBytesPerSecond = (long)Math.Round(value * 1024 * 1024)),
        Toggle(
            "Give way to whatever else is using the drives",
            "Whether the rebalancer stops while the pool is serving real IO and carries on when it "
            + "goes quiet.",
            "On, you should never notice it; it simply takes longer. Off, it keeps moving files "
            + "while you are using the pool, which finishes sooner and competes with you for the "
            + "drives.",
            defaultValue: true,
            "On: rebalancing pauses while the pool is busy and resumes when it is quiet.",
            "Off: rebalancing keeps going even while the pool is in use.",
            settings => settings.RebalancePauseWhileBusy ?? true,
            value => _settings.RebalancePauseWhileBusy = value,
            advanced: true),
        Numeric(
            "Difference that starts it",
            "How far apart the fullest and emptiest drive have to be, as a share of their size, "
            + "before rebalancing begins.",
            "Small means it reacts to a slight imbalance and moves files often. Large means it only "
            + "steps in when the pool is genuinely lopsided, and moves far less data over the life "
            + "of the pool.",
            minimum: 0.02,
            maximum: 0.9,
            step: 0.01,
            defaultValue: 0.15,
            SettingFormats.Percent,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"Rebalancing starts when the fullest and emptiest drive are more than {value:P0} apart."),
            settings => settings.RebalanceTriggerImbalance ?? 0.15,
            value => _settings.RebalanceTriggerImbalance = Math.Round(value, 3),
            advanced: true),
        Numeric(
            "Difference that stops it",
            "How level the drives have to be for rebalancing to consider itself finished. It is "
            + "always a smaller gap than the one that starts it.",
            "The gap between the two numbers is what stops it starting and stopping constantly. "
            + "Setting this very low makes it chase a perfectly level pool, moving files for "
            + "diminishing returns.",
            minimum: 0.005,
            maximum: 0.5,
            step: 0.005,
            defaultValue: 0.05,
            SettingFormats.Percent,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"It stops once the drives are within {value:P1} of each other."),
            settings => settings.RebalanceTargetImbalance ?? 0.05,
            value => _settings.RebalanceTargetImbalance = Math.Round(value, 3),
            advanced: true),
        Numeric(
            "Largest file it will move",
            "Files above this size are left where they are. Moving a very large file ties up both "
            + "drives for a long time for one file's worth of levelling.",
            "Raise it on a pool of disk images or video masters, where the big files are the "
            + "imbalance. Lower it to keep each move short, so a rebalance can stop promptly when "
            + "the pool gets busy.",
            minimum: 1,
            maximum: 1024,
            step: 1,
            defaultValue: 64,
            SettingFormats.Gigabytes,
            value => string.Create(
                CultureInfo.CurrentCulture,
                $"Files larger than {SettingFormats.Gigabytes(value)} are never moved by the rebalancer."),
            settings => (settings.RebalanceMaxFileBytes ?? 64L * 1024 * 1024 * 1024) / (double)(1024 * 1024 * 1024),
            value => _settings.RebalanceMaxFileBytes = (long)Math.Round(value * 1024 * 1024 * 1024),
            advanced: true));

    /// <summary>The word in the pill beside the slider: short enough to sit there, honest about
    /// which way the slider is leaning.</summary>
    private static string BiasLabel(double weight) => weight switch
    {
        < 0.35 => "free space only",
        < 0.85 => "mostly free space",
        < 1.6 => "balanced",
        < 2.4 => "leaning on speed",
        _ => "fastest drive",
    };

    private static string Bias(double value) => value switch
    {
        < 0.35 => "Fill the emptiest drive, whatever its speed",
        < 0.85 => "Mostly by free space, a little by speed",
        < 1.6 => "Balanced: free space and speed count equally",
        < 2.4 => "Prefer the faster drive unless it is much fuller",
        _ => "Strongly prefer the fastest drive",
    };

    /// <summary>The values MergePool ships with, which is what Defaults sends.</summary>
    private static PlacementSettingsDto Defaults() => new()
    {
        FreeSpaceWeight = 1.0,
        SpeedWeight = 1.0,
        MinFreeBytes = 2L * 1024 * 1024 * 1024,
        WriteHeadroomBytes = 256L * 1024 * 1024,
        ThrottleEnterRatio = 0.45,
        ThrottleExitRatio = 0.75,
        ThrottleCooldownSeconds = 60,
        LatencyThrottleRatio = 3.0,
        ThrottleEnterSamples = 3,
        ThrottleExitSamples = 3,
        MinSamplesForThrottleDetection = 5,
        EwmaAlpha = 0.25,
        BaselineAlpha = 0.02,
        BaselineRiseAlpha = 0.3,
        ReferenceThroughputBytesPerSecond = 120d * 1024 * 1024,
        MinSpeedFactor = 0.05,
        MaxSpeedFactor = 8.0,
        MinThroughputSampleBytes = 64 * 1024,
        IdleProbeIntervalSeconds = 120,
        IdleProbeBytes = 1 << 20,
        RebalanceEnabled = true,
        RebalanceMaxBytesPerSecond = 32L * 1024 * 1024,
        RebalanceTriggerImbalance = 0.15,
        RebalanceTargetImbalance = 0.05,
        RebalanceMaxFileBytes = 64L * 1024 * 1024 * 1024,
        RebalancePauseWhileBusy = true,
    };

    /// <summary>
    /// Whether the service stored something other than what was sent, which is how the window knows
    /// to say a value was brought into range.
    /// </summary>
    private static bool Differs(PlacementSettingsDto sent, PlacementSettingsDto stored) =>
        Moved(sent.FreeSpaceWeight, stored.FreeSpaceWeight)
        || Moved(sent.SpeedWeight, stored.SpeedWeight)
        || sent.MinFreeBytes != stored.MinFreeBytes
        || sent.WriteHeadroomBytes != stored.WriteHeadroomBytes
        || Moved(sent.ThrottleEnterRatio, stored.ThrottleEnterRatio)
        || Moved(sent.ThrottleExitRatio, stored.ThrottleExitRatio)
        || Moved(sent.ThrottleCooldownSeconds, stored.ThrottleCooldownSeconds)
        || Moved(sent.LatencyThrottleRatio, stored.LatencyThrottleRatio)
        || sent.ThrottleEnterSamples != stored.ThrottleEnterSamples
        || sent.ThrottleExitSamples != stored.ThrottleExitSamples
        || sent.MinSamplesForThrottleDetection != stored.MinSamplesForThrottleDetection
        || Moved(sent.EwmaAlpha, stored.EwmaAlpha)
        || Moved(sent.BaselineAlpha, stored.BaselineAlpha)
        || Moved(sent.BaselineRiseAlpha, stored.BaselineRiseAlpha)
        || Moved(sent.ReferenceThroughputBytesPerSecond, stored.ReferenceThroughputBytesPerSecond)
        || Moved(sent.MinSpeedFactor, stored.MinSpeedFactor)
        || Moved(sent.MaxSpeedFactor, stored.MaxSpeedFactor)
        || sent.MinThroughputSampleBytes != stored.MinThroughputSampleBytes
        || Moved(sent.IdleProbeIntervalSeconds, stored.IdleProbeIntervalSeconds)
        || sent.IdleProbeBytes != stored.IdleProbeBytes
        || Moved(sent.RebalanceTriggerImbalance, stored.RebalanceTriggerImbalance)
        || Moved(sent.RebalanceTargetImbalance, stored.RebalanceTargetImbalance)
        || sent.RebalanceMaxFileBytes != stored.RebalanceMaxFileBytes;

    private static bool Moved(double? sent, double? stored) =>
        sent is not null && stored is not null && Math.Abs(sent.Value - stored.Value) > 0.0001;
}
