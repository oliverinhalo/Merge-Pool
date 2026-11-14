// The whole web interface. It holds the access token in sessionStorage — closing the tab signs you
// out — and sends it on every call, because the server keeps no session of its own.
//
// Everything the desktop window can do is here: making and removing pools, adding and removing
// drives, every engine tunable, protected folders, sharing a pool on the network, and updates. The
// one deliberate exception is the web interface's own settings, which stay in the window: turning
// changes off or moving the port from this page would cut it off from the setting that undoes it.

const TOKEN_KEY = "mergepool.token";
const ADVANCED_KEY = "mergepool.advanced";

const el = (id) => document.getElementById(id);
const signin = el("signin");
const app = el("app");

let refreshTimer = null;

/** Everything the last refresh read, so a render never has to re-fetch. */
let state = {
  status: null,
  pools: [],
  drives: [],
  letters: [],
  settings: null,
  health: null,
  protectedByPool: {},
  shareByPool: {},
  updates: null,
};

/** Placement settings as edited here, before Save. Null when nothing has been touched. */
let draft = null;

let tab = "pools";
let advanced = false;

function token() {
  try {
    return sessionStorage.getItem(TOKEN_KEY) || "";
  } catch {
    return "";
  }
}

function setToken(value) {
  try {
    if (value) {
      sessionStorage.setItem(TOKEN_KEY, value);
    } else {
      sessionStorage.removeItem(TOKEN_KEY);
    }
  } catch {
    // Private browsing. The token then lives only for this page load, which still works.
  }
}

async function call(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: {
      "Authorization": "Bearer " + token(),
      "Content-Type": "application/json",
      ...(options.headers || {}),
    },
  });

  if (response.status === 401) {
    const error = new Error("The access token is not accepted.");
    error.unauthorized = true;
    throw error;
  }

  const body = await response.json().catch(() => ({}));

  if (!response.ok) {
    throw new Error(body.error || "MergePool could not do that.");
  }

  return body;
}

const post = (path, body) => call(path, { method: "POST", body: JSON.stringify(body ?? {}) });
const remove = (path) => call(path, { method: "DELETE" });

function bytes(value) {
  if (!value || value <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB", "PB"];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  return size.toFixed(size >= 100 || unit === 0 ? 0 : 1) + " " + units[unit];
}

function percent(part, whole) {
  if (!whole || whole <= 0) return 0;
  return Math.max(0, Math.min(100, (part / whole) * 100));
}

function escape(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  })[c]);
}

const MB = 1024 * 1024;
const GB = 1024 * MB;

function driveStatus(drive) {
  if (drive.state === "Offline") return { text: "Missing", css: "state-Missing" };
  if (drive.state === "Unavailable") return { text: "Not answering", css: "state-Degraded" };
  if (drive.state === "ReadOnly") return { text: "Read-only", css: "state-ReadOnly" };
  if (drive.throttled) {
    return {
      text: drive.throttleReason === "LatencySpike" ? "Throttled (latency)" : "Throttled (slow writes)",
      css: "state-throttled",
    };
  }
  return { text: "Online", css: "state-Online" };
}

function throughput(drive) {
  if (!drive.throughputBytesPerSecond || drive.throughputBytesPerSecond <= 0) return "—";
  return (drive.throughputBytesPerSecond / MB).toFixed(1) + " MB/s";
}

const readOnly = () => state.status?.readOnly === true;

/** Disabled attribute for anything that changes something, when the page may not. */
const ifWritable = () => (readOnly() ? "disabled" : "");

// ---------------------------------------------------------------------------
// The settings, described the same way the window describes them: what each
// one does, what moving it costs, and the value it ships with.
// ---------------------------------------------------------------------------

const pct = (v) => (v * 100).toFixed(v < 0.1 ? 1 : 0) + "%";
const mbs = (v) => Math.round(v / MB) + " MB/s";
const factor = (v) => (+v.toFixed(3)).toString();
const times = (v) => v.toFixed(1) + "×";
const samples = (v) => (v <= 1 ? "1 sample" : v + " samples");

function seconds(v) {
  if (v < 60) return Math.round(v) + " seconds";
  if (v < 3600) return Math.round(v / 60) + " minutes";
  return (v / 3600).toFixed(1) + " hours";
}

function minutes(v) {
  return v < 60 ? Math.round(v) + " minutes" : (v / 60).toFixed(1) + " hours";
}

function hours(v) {
  return v < 24 ? Math.round(v) + " hours" : (v / 24).toFixed(1) + " days";
}

const biasWord = (w) =>
  w < 0.35 ? "free space only"
  : w < 0.85 ? "mostly free space"
  : w < 1.6 ? "balanced"
  : w < 2.4 ? "leaning on speed"
  : "fastest drive";

const biasSentence = (w) =>
  w < 0.35 ? "Fill the emptiest drive, whatever its speed."
  : w < 0.85 ? "Mostly by free space, a little by speed."
  : w < 1.6 ? "Balanced: free space and speed count equally."
  : w < 2.4 ? "Prefer the faster drive unless it is much fuller."
  : "Strongly prefer the fastest drive.";

const PLACEMENT_GROUPS = [
  {
    title: "Where new files go",
    caption: "Every file written to the pool lands whole on one drive. These decide which one.",
    advanced: false,
    items: [
      {
        key: "speedWeight", title: "Empty drives or fast drives",
        min: 0, max: 3, step: 0.05, def: 1, fmt: biasWord, say: biasSentence,
        explain: "MergePool scores every drive for each new file and writes it to the winner. This is how much the drive's speed counts against how much room it has left.",
        effect: "All the way left fills the emptiest drive whatever its speed, which keeps the drives level. All the way right sends new files to the fastest drive until it is nearly full, which is quicker to write but leaves the drives uneven.",
      },
      {
        key: "minFreeBytes", title: "Space kept free on every drive",
        min: 0, max: 200 * GB, step: GB / 2, def: 2 * GB, fmt: bytes,
        say: (v) => v <= 0 ? "Drives are filled to the last byte." : `New files avoid a drive with less than ${bytes(v)} free.`,
        explain: "A drive with less than this much room left is passed over for new files. It is not a quota — nothing is moved off a drive to reach it — just a line below which a drive stops being offered new work.",
        effect: "A few gigabytes keeps Windows, and the drive itself, out of the trouble a completely full disk causes. Set it to zero and MergePool will fill a drive to the last byte.",
      },
      {
        key: "writeHeadroomBytes", title: "Room left beyond the file itself", advanced: true,
        min: 0, max: 8 * GB, step: 64 * MB, def: 256 * MB, fmt: bytes,
        say: (v) => `A drive needs the file's size plus ${bytes(v)} to be chosen for it.`,
        explain: "Headroom demanded on top of the size of the file being written. A drive that could take the file but not this much more besides is passed over for it.",
        effect: "Raising it keeps a little breathing room on every drive as files land. Setting it to zero lets a write fill a drive exactly, which can leave it with nothing spare for the file system's own bookkeeping.",
      },
      {
        key: "freeSpaceWeight", title: "How hard free space counts", advanced: true,
        min: 0, max: 3, step: 0.05, def: 1, fmt: factor,
        say: (v) => v < 0.05 ? "Free space is ignored; only speed picks the drive."
          : v < 0.95 ? "Free space counts, but less than in proportion."
          : v < 1.05 ? "A drive with twice the free space is twice as likely to be chosen."
          : "The emptiest drive wins all but the most lopsided comparisons.",
        explain: "The exponent on free space in the placement score. One means a drive with twice the room is twice as likely to be chosen; two means four times as likely.",
        effect: "Above one the emptiest drive wins nearly every file, which levels the drives fast and concentrates writes on one disk. At zero, free space stops counting altogether and only speed decides — the slider at the top of this group.",
      },
    ],
  },
  {
    title: "Slow drives",
    caption: "A drive that has suddenly got slower is parked: it keeps serving the files it holds, but no new ones are sent to it until it recovers. Each drive is judged against its own normal speed, so an archive disk is not treated as broken for being an archive disk.",
    advanced: false,
    items: [
      {
        key: "throttleEnterRatio", title: "How quickly a slow drive is parked",
        min: 0.1, max: 0.9, step: 0.05, def: 0.45, fmt: pct,
        say: (v) => `A drive is parked once it writes below ${pct(v)} of its own normal speed.`,
        explain: "How far below its own normal speed a drive has to fall before MergePool stops sending it new files. This is the symptom of an SMR disk whose cache has filled, a USB enclosure dropping to a slower mode, or a drive that is overheating.",
        effect: "Higher parks a drive at the first sign of a slowdown — responsive, but a brief stall can take a healthy drive out of rotation for a minute. Lower means only a drive that has nearly stopped is parked.",
      },
      {
        key: "throttleExitRatio", title: "Speed it has to regain to come back", advanced: true,
        min: 0.15, max: 0.99, step: 0.01, def: 0.75, fmt: pct,
        say: (v) => `A parked drive is used again once it is back above ${pct(v)} of its normal speed.`,
        explain: "A parked drive is used again once it writes at this much of its normal speed. It is deliberately higher than the figure that parked it.",
        effect: "The gap between the two is what stops a borderline drive flapping in and out of rotation. Narrow it and a drive returns sooner but less convincingly; widen it and a recovered drive sits out longer than it needs to.",
      },
      {
        key: "latencyThrottleRatio", title: "Slow responses count too", advanced: true,
        min: 1.2, max: 20, step: 0.1, def: 3, fmt: times,
        say: (v) => `Answering ${times(v)} slower than usual parks the drive, however fast it still writes.`,
        explain: "A drive is also parked when it takes this many times longer than usual to answer, even if the megabytes a second still look respectable. A disk doing its own housekeeping behaves exactly like this.",
        effect: "Lower it and MergePool reacts to a stuttering drive sooner. Raise it and only a drive that has become dramatically unresponsive is parked for latency.",
      },
      {
        key: "throttleEnterSamples", title: "Bad samples before parking a drive", advanced: true,
        min: 1, max: 20, step: 1, def: 3, fmt: samples,
        say: (v) => `${v} bad measurements in a row park a drive.`,
        explain: "How many measurements in a row have to look bad before the drive is parked. One unlucky write is not a slow drive.",
        effect: "One reacts immediately and will sometimes park a drive that was only briefly busy. Several is steadier, at the cost of sending a few more files to a drive that has genuinely gone slow.",
      },
      {
        key: "throttleExitSamples", title: "Good samples before using it again", advanced: true,
        min: 1, max: 20, step: 1, def: 3, fmt: samples,
        say: (v) => `${v} healthy measurements in a row bring a drive back.`,
        explain: "How many measurements in a row have to look healthy before a parked drive is given new files again.",
        effect: "More means a drive has to prove itself for longer before it is trusted with new files; fewer brings it back sooner on less evidence.",
      },
      {
        key: "throttleCooldownSeconds", title: "Shortest time parked", advanced: true,
        min: 5, max: 1800, step: 5, def: 60, fmt: seconds,
        say: (v) => `A parked drive sits out at least ${seconds(v)} before it can return.`,
        explain: "Once parked, a drive stays parked for at least this long, however good it starts to look. It gives a disk that is catching up with itself time to actually catch up.",
        effect: "Shorter puts a recovering drive back to work sooner. Longer leaves a drive that recovers in bursts — an SMR disk flushing its cache — properly out of the way.",
      },
      {
        key: "minSamplesForThrottleDetection", title: "Measurements before judging a drive at all", advanced: true,
        min: 1, max: 50, step: 1, def: 5, fmt: samples,
        say: (v) => `A drive is never called slow on fewer than ${v} measurements.`,
        explain: "A drive needs this many measurements before MergePool is willing to call it slow. Until then it has no normal speed to be compared against.",
        effect: "Lower it and a drive added mid-session is judged sooner, on thinner evidence. Raise it and a new drive is left alone for longer.",
      },
    ],
  },
  {
    title: "How drives are measured",
    caption: "What MergePool means by a drive's speed. It watches real writes as they happen and, when a drive has been idle, measures it with one small write of its own. These are worth touching only to explain a placement decision you disagree with.",
    advanced: true,
    items: [
      {
        key: "referenceThroughputBytesPerSecond", title: "Speed counted as normal", advanced: true,
        min: 10 * MB, max: 2000 * MB, step: 10 * MB, def: 120 * MB, fmt: mbs,
        say: (v) => `A drive writing ${mbs(v)} counts as ordinary speed.`,
        explain: "The throughput treated as a speed factor of 1.0. A drive measured at twice this is scored as twice as fast; one at half of it, half as fast.",
        effect: "It is a yardstick, not a limit: nothing is capped at this rate. Set it near what your typical drive actually achieves and the speed figures read sensibly.",
      },
      {
        key: "ewmaAlpha", title: "How fast the speed figure reacts", advanced: true,
        min: 0.01, max: 1, step: 0.01, def: 0.25, fmt: factor,
        say: (v) => v < 0.1 ? "The speed figure moves slowly and ignores short bursts."
          : v < 0.5 ? "The speed figure follows the last few measurements."
          : "The speed figure follows the newest measurement closely.",
        explain: "The weight given to the newest measurement when updating a drive's current speed. Everything below it is history.",
        effect: "High follows every burst and lull, so placement reacts quickly and twitchily. Low smooths the figure out, so a drive's score reflects the last few minutes rather than the last few seconds.",
      },
      {
        key: "baselineAlpha", title: "How fast normal speed drifts down", advanced: true,
        min: 0.001, max: 0.5, step: 0.001, def: 0.02, fmt: factor,
        say: (v) => v < 0.01 ? "Normal speed is close to fixed: a slowdown stays visible for a long time."
          : v < 0.1 ? "Normal speed drifts slowly, so a real slowdown is still noticed."
          : "Normal speed follows the drive down, so sustained slowness becomes the new normal.",
        explain: "A drive's baseline — what MergePool considers normal for it — is deliberately slow to fall, so an hour of heavy writing does not redefine a healthy drive as a slow one.",
        effect: "Raise it and the baseline follows a drive downhill, which stops it being parked but also stops a real slowdown being noticed. Lower it and the baseline is near enough fixed at the drive's best behaviour.",
      },
      {
        key: "baselineRiseAlpha", title: "How fast normal speed rises", advanced: true,
        min: 0.01, max: 1, step: 0.01, def: 0.3, fmt: factor,
        say: (v) => v < 0.15 ? "A drive's best speed is learned gradually."
          : v < 0.6 ? "A faster measurement raises what is expected within a few samples."
          : "One fast measurement becomes the drive's new normal almost at once.",
        explain: "The same baseline, going the other way: how readily it accepts a measurement better than itself. It rises faster than it falls on purpose.",
        effect: "High means a drive that has just done something fast is immediately expected to keep it up, and judged against that. Low means a drive earns its reputation slowly.",
      },
      {
        key: "minSpeedFactor", title: "Slowest a drive can be scored", advanced: true,
        min: 0.001, max: 1, step: 0.001, def: 0.05, fmt: factor,
        say: (v) => `The slowest drive still scores at least ${factor(v)} against a normal drive's 1.`,
        explain: "A floor under the measured speed factor. However badly a drive performs, it never scores below this, so it keeps a small share of new files rather than none.",
        effect: "Near zero lets a struggling drive be ignored while it is struggling. Raising it keeps work flowing to a slow drive, which is what you want if it is slow but fine and holds the content you are writing.",
      },
      {
        key: "maxSpeedFactor", title: "Fastest a drive can be scored", advanced: true,
        min: 1, max: 64, step: 0.5, def: 8, fmt: factor,
        say: (v) => `The fastest drive is scored at most ${factor(v)}× a normal drive.`,
        explain: "A ceiling on the measured speed factor, so one very fast drive — or one lucky measurement on a cached write — cannot take every new file.",
        effect: "Lower it to spread writes more evenly across mixed hardware. Raise it to let a genuinely much faster drive, an NVMe among spinning disks, take the share its speed suggests.",
      },
      {
        key: "minThroughputSampleBytes", title: "Smallest write that says anything about speed", advanced: true,
        min: 4 * 1024, max: 4096 * 1024, step: 4 * 1024, def: 64 * 1024, fmt: bytes,
        say: (v) => `Only writes of ${bytes(v)} or more shape the speed figure.`,
        explain: "Writes below this size are too small to measure throughput with — they finish inside the drive's own cache. They still count towards how quickly it responds.",
        effect: "Raising it means only substantial writes shape the speed figure, which is more accurate but gathers evidence more slowly on a pool that mostly stores small files.",
      },
      {
        key: "idleProbeIntervalSeconds", title: "How often an idle drive is measured", advanced: true,
        min: 10, max: 7200, step: 10, def: 120, fmt: seconds,
        say: (v) => `An idle drive is measured again after ${seconds(v)}.`,
        explain: "A drive nobody is writing to tells you nothing about its speed. After this long idle, MergePool writes one small file to it to find out.",
        effect: "Shorter keeps the speed figures fresh on a pool that is mostly quiet. Longer leaves idle disks alone — worth it if they spin down, since a probe wakes them up.",
      },
      {
        key: "idleProbeBytes", title: "Size of that measurement", advanced: true,
        min: 64 * 1024, max: 64 * MB, step: 64 * 1024, def: MB, fmt: bytes,
        say: (v) => `Each probe writes ${bytes(v)} and deletes it again.`,
        explain: "How much the idle probe writes. It is written inside the pool's own folder and deleted straight away.",
        effect: "Bigger is a better measurement and more honest about a drive that is slow in a sustained way. Smaller costs an SMR disk almost nothing, which is why the default is as small as it is.",
      },
    ],
  },
  {
    title: "Evening out the drives",
    caption: "Placement keeps new files off the fullest drives, but it cannot undo a pool that was already lopsided. The rebalancer moves files between drives in the background to fix that, at a rate it is not allowed to exceed, skipping anything in use.",
    advanced: false,
    items: [
      {
        key: "rebalanceEnabled", title: "Even out the drives in the background", toggle: true, def: true,
        on: "On: files move between drives in the background to level them up.",
        off: "Off: nothing is moved unless you start a rebalance yourself.",
        explain: "Whether MergePool moves files off the fullest drives on its own. Files are moved whole, one drive to another, with the copy verified before the original is removed.",
        effect: "On, the drives converge over hours or days without you doing anything. Off, nothing moves unless you press Rebalance — which is what you want while you are copying a lot of data in.",
      },
      {
        key: "rebalanceMaxBytesPerSecond", title: "Fastest it may move files",
        min: MB, max: 500 * MB, step: MB, def: 32 * MB, fmt: mbs,
        say: (v) => `Rebalancing moves at most ${mbs(v)}, so it stays out of the way of real work.`,
        explain: "A ceiling on what the rebalancer reads and writes. It is not a target: when the drives are level it moves nothing at all.",
        effect: "Low keeps it invisible, and a lopsided pool takes days to even out. High levels the pool quickly and is noticeable while it does — in disk noise, and in anything else reading from those drives.",
      },
      {
        key: "rebalancePauseWhileBusy", title: "Give way to whatever else is using the drives",
        toggle: true, def: true, advanced: true,
        on: "On: rebalancing pauses while the pool is busy and resumes when it is quiet.",
        off: "Off: rebalancing keeps going even while the pool is in use.",
        explain: "Whether the rebalancer stops while the pool is serving real IO and carries on when it goes quiet.",
        effect: "On, you should never notice it; it simply takes longer. Off, it keeps moving files while you are using the pool, which finishes sooner and competes with you for the drives.",
      },
      {
        key: "rebalanceTriggerImbalance", title: "Difference that starts it", advanced: true,
        min: 0.02, max: 0.9, step: 0.01, def: 0.15, fmt: pct,
        say: (v) => `Rebalancing starts when the fullest and emptiest drive are more than ${pct(v)} apart.`,
        explain: "How far apart the fullest and emptiest drive have to be, as a share of their size, before rebalancing begins.",
        effect: "Small means it reacts to a slight imbalance and moves files often. Large means it only steps in when the pool is genuinely lopsided, and moves far less data over the life of the pool.",
      },
      {
        key: "rebalanceTargetImbalance", title: "Difference that stops it", advanced: true,
        min: 0.005, max: 0.5, step: 0.005, def: 0.05, fmt: pct,
        say: (v) => `It stops once the drives are within ${pct(v)} of each other.`,
        explain: "How level the drives have to be for rebalancing to consider itself finished. It is always a smaller gap than the one that starts it.",
        effect: "The gap between the two numbers is what stops it starting and stopping constantly. Setting this very low makes it chase a perfectly level pool, moving files for diminishing returns.",
      },
      {
        key: "rebalanceMaxFileBytes", title: "Largest file it will move", advanced: true,
        min: GB, max: 1024 * GB, step: GB, def: 64 * GB, fmt: bytes,
        say: (v) => `Files larger than ${bytes(v)} are never moved by the rebalancer.`,
        explain: "Files above this size are left where they are. Moving a very large file ties up both drives for a long time for one file's worth of levelling.",
        effect: "Raise it on a pool of disk images or video masters, where the big files are the imbalance. Lower it to keep each move short, so a rebalance can stop promptly when the pool gets busy.",
      },
    ],
  },
];

const DUPLICATION_ITEMS = [
  {
    key: "maxBytesPerSecond", title: "Fastest it may copy", advanced: true,
    min: MB, max: 500 * MB, step: MB, def: 32 * MB, fmt: mbs,
    say: (v) => `Copying moves at most ${mbs(v)}.`,
    explain: "A ceiling on what making copies reads and writes. Copying happens in the background, a file at a time, and a file something has open is left for the next sweep.",
    effect: "Low keeps copying invisible and means a newly protected folder takes longer to be safe. High gets there sooner and is noticeable on the drives while it does.",
  },
  {
    key: "scanIntervalMinutes", title: "How often it checks the protected folders", advanced: true,
    min: 1, max: 1440, step: 1, def: 15, fmt: minutes,
    say: (v) => `The protected folders are checked over every ${minutes(v)}.`,
    explain: "How long MergePool waits between sweeps of your protected folders, looking for files with too few copies, copies that are out of date, and copies no rule asks for any more.",
    effect: "Often means a new file is protected within minutes, at the cost of walking those folders that often. Rarely is lighter on the drives and leaves a new file unprotected for longer.",
  },
  {
    key: "copyHeadroomBytes", title: "Room left after a copy lands", advanced: true,
    min: 0, max: 8 * GB, step: 64 * MB, def: 256 * MB, fmt: bytes,
    say: (v) => v <= 0 ? "A copy may fill a drive completely." : `A drive keeps at least ${bytes(v)} free after a copy lands on it.`,
    explain: "Free space a drive must still have once a copy has been written to it. A drive that would be left with less than this is not chosen to hold the copy.",
    effect: "Raising it keeps the drives from being filled to the brim by copies. Lowering it lets copies use almost all of a drive.",
  },
  {
    key: "pauseWhileBusy", title: "Give way to whatever else is using the drives",
    toggle: true, def: true, advanced: true,
    on: "On: copying pauses while the pool is busy and resumes when it is quiet.",
    off: "Off: copying keeps going even while the pool is in use.",
    explain: "Whether copying stops while the pool is serving real IO and carries on when it goes quiet.",
    effect: "On, you should never notice it; protection simply arrives later. Off, copying competes with whatever you are doing and finishes sooner.",
  },
];

const UPDATE_ITEMS = [
  {
    key: "checkIntervalHours", title: "How often it looks for a new version", advanced: true,
    min: 1, max: 336, step: 1, def: 6, fmt: hours,
    say: (v) => `The release feed is checked every ${hours(v)}.`,
    explain: "How long MergePool waits between checks of the release feed. A check is one small request; it downloads nothing unless there is a newer version and you have asked for it to be installed.",
    effect: "More often means you get a fix sooner. Less often means next to no network traffic — and you can always press Check now.",
  },
  {
    key: "includePrereleases", title: "Offer pre-release versions", toggle: true, def: false, advanced: true,
    on: "On: pre-releases are installed like any other version.",
    off: "Off: only finished releases are offered.",
    explain: "Whether a version marked as a pre-release counts as an update. These are builds put out for testing before a release is called finished.",
    effect: "On, you see fixes and new work first, and you are the one finding out what is wrong with them. Off, MergePool only ever moves to a finished release.",
  },
];

/** One setting, rendered the way the window renders it. */
function renderSetting(item, value, scope) {
  if (item.advanced && !advanced) return "";

  const isDefault = item.toggle
    ? value === item.def
    : Math.abs(value - item.def) < (item.step || 1) / 2;

  const head = `
    <div class="head">
      <span class="title">${escape(item.title)}</span>
      ${isDefault ? "" : `<span class="changed">changed</span>`}
      ${item.toggle ? "" : `<span class="value">${escape(item.fmt(value))}</span>`}
    </div>`;

  const control = item.toggle
    ? `<label class="switch" style="margin-top:10px">
         <input type="checkbox" data-set="${escape(scope)}:${escape(item.key)}" ${value ? "checked" : ""} ${ifWritable()} />
         <span class="track"></span>
       </label>`
    : `<input type="range" data-set="${escape(scope)}:${escape(item.key)}"
              min="${item.min}" max="${item.max}" step="${item.step}" value="${value}" ${ifWritable()} />
       <div class="ends"><span>${escape(item.fmt(item.min))}</span><span>${escape(item.fmt(item.max))}</span></div>`;

  const summary = item.toggle ? (value ? item.on : item.off) : item.say(value);
  const ships = item.toggle ? (item.def ? "on" : "off") : item.fmt(item.def);

  return `
    <div class="setting">
      ${head}
      ${control}
      <p class="summary">${escape(summary)}</p>
      <p class="explain">${escape(item.explain)}</p>
      <p class="effect">${escape(item.effect)}</p>
      <div class="ships">
        <span>Ships as ${escape(ships)}</span>
        ${isDefault || readOnly() ? "" : `<button data-reset="${escape(scope)}:${escape(item.key)}">Put this one back</button>`}
      </div>
    </div>`;
}

function renderSettings() {
  if (!state.settings) return;

  const placement = draft ?? state.settings.placement;
  const duplication = state.settings.duplication;
  const updates = state.settings.updates;
  const web = state.settings.webInterface;

  const total = PLACEMENT_GROUPS.reduce((n, g) => n + g.items.length, 0)
    + DUPLICATION_ITEMS.length + UPDATE_ITEMS.length;

  el("settings-scope").textContent = advanced
    ? `Showing all ${total} settings, expert ones included. Each one says what it does and what it ships with, and the service refuses a value that would stop it working.`
    : "Showing the settings worth changing day to day. Turn on Advanced, at the top, for every tunable the engine has.";

  const groups = PLACEMENT_GROUPS.map((group) => {
    if (group.advanced && !advanced) return "";

    const items = group.items.map((item) => renderSetting(item, placement[item.key], "placement")).join("");
    if (!items) return "";

    const changed = group.items.filter((item) => {
      const v = placement[item.key];
      return item.toggle ? v !== item.def : Math.abs(v - item.def) >= (item.step || 1) / 2;
    }).length;

    return `
      <div class="group-head">
        <h2>${escape(group.title)}</h2>
        <span class="count">${changed === 0 ? "every setting here is at its default"
          : changed === 1 ? "1 setting changed from its default"
          : changed + " settings changed from their defaults"}</span>
      </div>
      <p class="muted" style="margin:0 0 10px">${escape(group.caption)}</p>
      ${items}`;
  }).join("");

  // Copying and updating apply the moment they are changed, like the window: they are the
  // service's own behaviour rather than placement decisions waiting on Save.
  const copying = `
    <div class="group-head"><h2>Protecting folders</h2></div>
    <p class="muted" style="margin:0 0 10px">
      How the copying itself is paced. These take effect the moment you change them.
    </p>
    <div class="setting">
      <div class="head"><span class="title">Keep protected folders up to date in the background</span></div>
      <label class="switch" style="margin-top:10px">
        <input type="checkbox" data-set="duplication:enabled" ${duplication.enabled ? "checked" : ""} ${ifWritable()} />
        <span class="track"></span>
      </label>
      <p class="summary">${duplication.enabled
        ? "On: copies are made and kept up to date in the background."
        : "Off: all copying is paused everywhere. The copies already made are left alone."}</p>
      <p class="explain">The master switch for copying, across every pool and every protected folder.</p>
      <p class="effect">Off, the rules stay exactly as they are and nothing is copied or removed until you turn it back on.</p>
    </div>
    ${DUPLICATION_ITEMS.map((item) => renderSetting(item, duplication[item.key], "duplication")).join("")}`;

  const updating = `
    <div class="group-head"><h2>Updating</h2></div>
    <p class="muted" style="margin:0 0 10px">
      How MergePool looks for new versions of itself. These take effect at once.
    </p>
    <div class="setting">
      <div class="head"><span class="title">Check for new versions automatically</span></div>
      <label class="switch" style="margin-top:10px">
        <input type="checkbox" data-set="updates:automaticChecks" ${updates.automaticChecks ? "checked" : ""} ${ifWritable()} />
        <span class="track"></span>
      </label>
      <p class="summary">${updates.automaticChecks ? "On: the release feed is checked on a timer." : "Off: nothing is checked unless you press Check now."}</p>
      <p class="explain">One small request on a timer; it downloads nothing unless there is something newer.</p>
      <p class="effect">Off means you decide when to look, from the Updates tab.</p>
    </div>
    <div class="setting">
      <div class="head"><span class="title">Install them without asking</span></div>
      <label class="switch" style="margin-top:10px">
        <input type="checkbox" data-set="updates:automaticInstall" ${updates.automaticInstall ? "checked" : ""} ${ifWritable()} />
        <span class="track"></span>
      </label>
      <p class="summary">${updates.automaticInstall ? "On: a newer version installs itself when it is found." : "Off: a newer version waits for you to install it."}</p>
      <p class="explain">An update only adds a new version folder and switches which one runs — your pools and files are never touched, and a version that fails to come up is rolled back.</p>
      <p class="effect">On is how a machine you are not watching stays current. Off puts the moment in your hands.</p>
    </div>
    ${UPDATE_ITEMS.map((item) => renderSetting(item, updates[item.key], "updates")).join("")}`;

  const webPanel = `
    <div class="group-head"><h2>This web interface</h2></div>
    <div class="setting">
      <p class="summary" style="margin-top:0">
        Serving on port ${escape(web.port)}, reachable from
        ${escape(web.scope === "Network" ? "any device on your network" : "this computer only")},
        and ${escape(web.allowChanges ? "allowed to make changes" : "set to look but not touch")}.
      </p>
      <p class="explain">${escape(web.why)}</p>
    </div>`;

  el("settings-groups").innerHTML = groups + copying + updating + webPanel;

  el("save-settings").disabled = readOnly() || draft === null;
  el("reset-settings").disabled = readOnly();
  el("settings-message").textContent = draft === null
    ? "Saved. The change applies to the next file placed; nothing already in the pool moves."
    : "Not saved yet — press Save settings to send these to the service.";
}

// ---------------------------------------------------------------------------
// Pools
// ---------------------------------------------------------------------------

function renderPool(pool) {
  const poolPercent = percent(pool.poolUsedBytes, pool.poolCapacityBytes || pool.totalBytes);
  const drivePercent = percent(pool.totalBytes - pool.freeBytes, pool.totalBytes);

  const drives = pool.drives.map((drive) => {
    const status = driveStatus(drive);
    const pooled = drive.poolBytes ?? 0;
    const used = Math.max(0, drive.totalBytes - drive.freeBytes);

    return `
      <div class="row">
        <div class="letter">${escape(drive.letter ? drive.letter + ":" : "—")}</div>
        <div class="body">
          <div class="name">${escape(drive.label || "(no label)")}</div>
          <div class="bar">
            <i class="other" style="width:${percent(used, drive.totalBytes)}%"></i>
            <i class="pool" style="width:${percent(pooled, drive.totalBytes)}%"></i>
          </div>
          <div class="muted">
            ${drive.poolBytes === null || drive.poolBytes === undefined
              ? "Measuring what the pool holds here…"
              : `${bytes(pooled)} pooled · ${bytes(Math.max(0, used - pooled))} other files · ${bytes(drive.freeBytes)} free`}
          </div>
        </div>
        <div class="side">
          <div class="${status.css}">${escape(status.text)}</div>
          <div class="muted">${escape(throughput(drive))}</div>
          ${readOnly() ? "" : `
            <div style="margin-top:6px;display:flex;gap:6px;flex-wrap:wrap">
              <button data-drive="${escape(drive.volumeId)}" data-mode="detach"
                      title="The drive leaves the pool. Its files stay on it, untouched, in its pool part folder.">Remove</button>
              <button class="danger" data-drive="${escape(drive.volumeId)}" data-mode="evacuate"
                      title="Moves this drive's pooled files onto the pool's other drives first, then removes it.">Move off</button>
            </div>`}
        </div>
      </div>`;
  }).join("");

  const share = state.shareByPool[pool.poolId];

  return `
    <section class="card" data-pool="${escape(pool.poolId)}">
      <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
        <h2 style="margin:0">${escape(pool.name)}</h2>
        <span class="pill" style="margin-left:0;background:var(--accent-soft);color:var(--accent)">
          ${escape(pool.mounted ? pool.mountPoint : "not mounted")}
        </span>
        <span class="state-${escape(pool.health)}" style="margin-left:auto">${escape(pool.health)}</span>
      </div>

      <div class="cols" style="margin-top:16px">
        <div>
          <p class="label">POOL</p>
          <div class="metric">${bytes(pool.poolUsedBytes)}</div>
          <div class="bar"><i class="pool" style="width:${poolPercent}%"></i></div>
          <div class="muted">
            ${pool.measured
              ? `${bytes(pool.poolUsedBytes)} of ${bytes(pool.poolCapacityBytes)} used by this pool`
              : "Measuring what the pool holds…"}
          </div>
        </div>
        <div>
          <p class="label">DRIVES IN TOTAL</p>
          <div class="metric" style="color:var(--muted)">${bytes(pool.totalBytes)}</div>
          <div class="bar"><i class="other" style="width:${drivePercent}%"></i></div>
          <div class="muted">${bytes(pool.totalBytes - pool.freeBytes)} of ${bytes(pool.totalBytes)} used on these drives in total</div>
        </div>
      </div>

      <p class="label" style="margin-top:18px">DRIVES IN THIS POOL</p>
      ${drives}

      ${renderSharePanel(pool, share)}

      <div class="actions">
        <button data-act="mount" ${pool.mounted || readOnly() ? "disabled" : ""}>Mount</button>
        <button data-act="unmount" ${pool.mounted && !readOnly() ? "" : "disabled"}>Unmount</button>
        <button data-act="rebalance" ${ifWritable()}>Rebalance</button>
        <button data-act="remount" ${ifWritable()}
                title="Takes the mount down and brings it straight back. The recovery for a pool that has stopped answering; open handles on it break, every file stays where it is.">Remount</button>
        <button class="danger" data-act="remove" ${ifWritable()}
                title="Forgets the pool. Every file stays on its drive, in that drive's pool part folder. Nothing is deleted.">Remove pool</button>
      </div>
      <p class="muted" data-pool-message></p>
    </section>`;
}

function renderSharePanel(pool, share) {
  if (!share) return "";

  const accounts = share.allowedAccounts.length === 0
    ? `<p class="muted" style="margin:6px 0">No account may connect yet, so nothing is shared.</p>`
    : share.allowedAccounts.map((account) => `
        <div class="row" style="padding:7px 10px">
          <div class="body"><div class="name">${escape(account)}</div></div>
          ${readOnly() ? "" : `<button class="danger" data-share-remove="${escape(account)}">Remove</button>`}
        </div>`).join("");

  return `
    <details style="margin-top:14px" ${share.enabled ? "open" : ""}>
      <summary style="cursor:pointer;font-weight:600;font-size:13px">Share this pool on the network</summary>
      <div class="inset" style="margin-top:10px">
        <label class="switch" style="align-items:flex-start">
          <input type="checkbox" data-share-enabled ${share.enabled ? "checked" : ""} ${ifWritable()} />
          <span class="track"></span>
          <span style="font-size:12px;color:var(--muted)">
            Share this pool over SMB while it is mounted. The share follows the mount, so nothing
            has to put it back. This is how Docker and WSL2 reach a pool — see docs/DOCKER.md.
          </span>
        </label>

        <div class="grid2" style="margin-top:12px">
          <div class="field">
            <p class="label">SHARE NAME</p>
            <input type="text" data-share-name value="${escape(share.name)}"
                   placeholder="${escape(share.effectiveName || pool.name)}" ${ifWritable()} />
          </div>
          <div class="field" style="display:flex;align-items:flex-end">
            <label class="switch">
              <input type="checkbox" data-share-readonly ${share.readOnly ? "checked" : ""} ${ifWritable()} />
              <span class="track"></span>
              <span style="font-size:12px;color:var(--muted)">Read-only</span>
            </label>
          </div>
        </div>

        <p class="label">WHO MAY CONNECT</p>
        <p class="muted" style="margin:0 0 6px;font-size:12px">
          Windows accounts, as MACHINE\\user or just user. Use a dedicated local account rather than
          your own sign-in. MergePool never grants everyone and never guesses an account.
        </p>
        ${accounts}
        ${readOnly() ? "" : `
          <div style="display:flex;gap:8px;margin-top:8px">
            <input type="text" data-share-account placeholder="MACHINE\\poolsvc" />
            <button data-share-add>Allow</button>
          </div>`}

        ${share.problem ? `<p class="banner warn" style="margin-top:10px">${escape(share.problem)}</p>` : ""}
        <p class="muted" data-share-message></p>
      </div>
    </details>`;
}

function renderIoHealth() {
  const pools = state.health?.pools ?? [];
  const unwell = pools.filter((pool) => pool.state === "Stalled" || pool.state === "Slow");

  if (unwell.length === 0) {
    el("io-health").innerHTML = "";
    return;
  }

  el("io-health").innerHTML = unwell.map((pool) => `
    <div class="banner ${pool.state === "Stalled" ? "" : "warn"}">
      <strong>${escape(pool.name)}</strong>: ${escape(pool.summary)}
      ${pool.recentFaults.length ? `<div class="mono" style="margin-top:8px">${escape(pool.recentFaults.join("\n"))}</div>` : ""}
    </div>`).join("");
}

// ---------------------------------------------------------------------------
// Drives, and making a pool
// ---------------------------------------------------------------------------

function renderDrives() {
  const free = state.drives.filter((drive) => drive.poolable && !drive.poolId);

  el("new-pool-letter").innerHTML = state.letters.length === 0
    ? `<option value="">No free letters</option>`
    : state.letters.map((letter) => `<option value="${escape(letter)}">${escape(letter)}:</option>`).join("");

  el("new-pool-drives").innerHTML = free.length === 0
    ? `<p class="muted">Every drive that can be pooled already is.</p>`
    : free.map((drive) => `
        <label>
          <input type="checkbox" value="${escape(drive.volumeId)}" ${ifWritable()} />
          <span>
            <strong>${escape(drive.letter ? drive.letter + ":" : "—")}</strong>
            ${escape(drive.label || "(no label)")}
            <span class="muted">· ${bytes(drive.freeBytes)} free of ${bytes(drive.totalBytes)} · ${escape(drive.fileSystem)}</span>
          </span>
        </label>`).join("");

  el("create-pool").disabled = readOnly() || free.length === 0 || state.letters.length === 0;

  el("free-drives").innerHTML = state.drives.map((drive) => {
    const pool = state.pools.find((candidate) => candidate.poolId === drive.poolId);

    return `
      <div class="row">
        <div class="letter">${escape(drive.letter ? drive.letter + ":" : "—")}</div>
        <div class="body">
          <div class="name">${escape(drive.label || "(no label)")}</div>
          <div class="muted">${bytes(drive.freeBytes)} free of ${bytes(drive.totalBytes)} · ${escape(drive.fileSystem)} · ${escape(drive.kind)}</div>
        </div>
        <div class="side">
          ${pool ? `<span class="muted">in ${escape(pool.name)}</span>`
            : drive.poolable
              ? (readOnly() ? `<span class="muted">free</span>`
                 : `<select data-add="${escape(drive.volumeId)}" aria-label="Add ${escape(drive.label)} to a pool"></select>`)
              : `<span class="muted">cannot be pooled</span>`}
        </div>
      </div>`;
  }).join("");

  wireAddDrive();
}

// ---------------------------------------------------------------------------
// Protected folders
// ---------------------------------------------------------------------------

function renderProtected() {
  if (state.pools.length === 0) {
    el("protected-pools").innerHTML = `<div class="card"><p class="muted">No pools yet.</p></div>`;
    return;
  }

  el("protected-pools").innerHTML = state.pools.map((pool) => {
    const data = state.protectedByPool[pool.poolId];
    if (!data) return "";

    const folders = data.folders.length === 0
      ? `<p class="muted">No folder in this pool is protected yet.</p>`
      : data.folders.map((folder) => `
          <div class="row" data-folder="${escape(folder.id)}">
            <div class="body">
              <div class="name">${escape(folder.folder || "(the whole pool)")}</div>
              <div class="muted">
                ${folder.everyDrive ? "A copy on every drive" : folder.copies + " copies"} ·
                ${folder.protectedFiles} of ${folder.files} files have them ·
                ${bytes(folder.bytes)}
                ${folder.pendingBytes > 0 ? ` · ${bytes(folder.pendingBytes)} still to copy` : ""}
              </div>
              ${folder.problems.length ? `<div class="mono" style="margin-top:6px">${escape(folder.problems.slice(0, 3).join("\n"))}</div>` : ""}
            </div>
            <div class="side">
              <div class="${folder.satisfied ? "state-Online" : "state-Degraded"}">${folder.satisfied ? "Protected" : "Working"}</div>
              ${readOnly() ? "" : `<button class="danger" data-stop="${escape(folder.id)}" style="margin-top:6px">Stop</button>`}
            </div>
          </div>`).join("");

    return `
      <section class="card" data-protected-pool="${escape(pool.poolId)}">
        <h2>${escape(pool.name)}</h2>
        <p class="muted" style="margin-top:0">
          Pick a folder inside the pool and MergePool keeps every file in it on several drives at
          once. Lose a drive and the files are still there, on the others. This is not a backup of
          your computer: the copies live on the pool's own drives.
        </p>

        ${data.copiesToAdd + data.copiesToRefresh + data.copiesToRemove > 0
          ? `<p class="muted">${data.copiesToAdd} to make, ${data.copiesToRefresh} to bring up to date, ${data.copiesToRemove} to remove · ${bytes(data.bytesToCopy)}</p>`
          : `<p class="muted">Every protected folder has the copies it asks for.</p>`}

        ${folders}

        ${readOnly() ? "" : `
          <div class="inset" style="margin-top:14px">
            <p class="label">PROTECT A FOLDER</p>
            <div class="field">
              <input type="text" data-new-folder placeholder="Photos, or Photos\\2026 — as it appears inside the pool" />
            </div>
            <div class="grid2">
              <div class="field">
                <p class="label">COPIES</p>
                <input type="number" data-new-copies value="2" min="2" max="8" />
              </div>
              <div class="field" style="display:flex;align-items:flex-end;gap:14px;flex-wrap:wrap">
                <label class="switch"><input type="checkbox" data-new-every /><span class="track"></span><span style="font-size:12px;color:var(--muted)">Every drive</span></label>
                <label class="switch"><input type="checkbox" data-new-verify /><span class="track"></span><span style="font-size:12px;color:var(--muted)">Byte for byte</span></label>
                <label class="switch"><input type="checkbox" data-new-keep /><span class="track"></span><span style="font-size:12px;color:var(--muted)">Never delete a copy</span></label>
              </div>
            </div>
            <div class="actions">
              <button class="primary" data-protect>Protect this folder</button>
              <button data-protect-all title="Every file in the pool, including ones added later.">Protect the whole pool</button>
              <button data-run-now title="Go over the protected folders now rather than waiting for the next sweep.">Check now</button>
            </div>
            <p class="muted" data-protected-message></p>
          </div>`}
      </section>`;
  }).join("");
}

// ---------------------------------------------------------------------------
// Updates
// ---------------------------------------------------------------------------

function renderUpdates() {
  const update = state.updates;

  if (!update) {
    el("updates-panel").innerHTML = `
      <div class="card"><p class="muted">
        This MergePool is not running from a managed install, so it cannot update itself.
      </p></div>`;
    return;
  }

  el("updates-panel").innerHTML = `
    <section class="card">
      <h2>Updates</h2>
      <p class="summary" style="margin-top:0">
        ${update.updateAvailable
          ? `MergePool ${escape(update.available)} is available. You are on ${escape(update.installed)}.`
          : `You are on the newest version (${escape(update.installed)}).`}
      </p>
      <p class="muted">
        Installing keeps every file where it is. The service restarts on the new version and your
        pools come straight back; a version that does not come up healthy is rolled back.
      </p>
      ${update.stage && update.stage !== "Idle" ? `<p class="muted">Stage: ${escape(update.stage)}${update.progress ? ` · ${Math.round(update.progress * 100)}%` : ""}</p>` : ""}
      ${update.lastError ? `<p class="banner">${escape(update.lastError)}</p>` : ""}
      <div class="actions">
        <button id="check-update" ${ifWritable()}>Check now</button>
        <button class="primary" id="apply-update" ${update.updateAvailable && !readOnly() ? "" : "disabled"}>Install now</button>
      </div>
      <p class="muted" id="update-message"></p>
    </section>`;

  el("check-update")?.addEventListener("click", async () => {
    await guard(el("update-message"), async () => {
      state.updates = await post("/api/update/check");
      renderUpdates();
    });
  });

  el("apply-update")?.addEventListener("click", async () => {
    await guard(el("update-message"), async () => {
      state.updates = await post("/api/update/apply");
      renderUpdates();
    });
  });
}

// ---------------------------------------------------------------------------
// Wiring
// ---------------------------------------------------------------------------

/** Runs an action, putting whatever goes wrong next to the thing that was pressed. */
async function guard(messageNode, action) {
  if (messageNode) messageNode.textContent = "Working…";

  try {
    await action();
    if (messageNode) messageNode.textContent = "";
  } catch (error) {
    if (error.unauthorized) {
      signOut();
      return;
    }

    if (messageNode) messageNode.textContent = error.message;
  }
}

function wirePoolButtons() {
  document.querySelectorAll("[data-pool] [data-act]").forEach((button) => {
    button.addEventListener("click", async () => {
      const card = button.closest("[data-pool]");
      const poolId = card.dataset.pool;
      const action = button.dataset.act;
      const message = card.querySelector("[data-pool-message]");
      const pool = state.pools.find((candidate) => candidate.poolId === poolId);

      if (action === "remove" && !confirm(
        `Forget the pool "${pool?.name}"?\n\nEvery file stays on the drive it is on, in that drive's `
        + `pool part folder. Nothing is deleted, and you can make the pool again from the same drives.`)) {
        return;
      }

      if (action === "remount" && !confirm(
        `Remount "${pool?.name}"?\n\nThe drive letter goes away and comes back. Every file stays `
        + `where it is, but anything with a file open on the pool will see it disappear.`)) {
        return;
      }

      await guard(message, async () => {
        if (action === "remove") {
          await remove(`/api/pools/${poolId}`);
        } else {
          await post(`/api/pools/${poolId}/${action}`);
        }

        await refresh();
      });
    });
  });

  document.querySelectorAll("[data-pool] [data-drive]").forEach((button) => {
    button.addEventListener("click", async () => {
      const card = button.closest("[data-pool]");
      const poolId = card.dataset.pool;
      const message = card.querySelector("[data-pool-message]");
      const volumeId = button.dataset.drive;
      const move = button.dataset.mode === "evacuate";

      const warning = move
        ? "Move this drive's pooled files onto the pool's other drives, then remove it?\n\nEach file is "
          + "copied whole and only then removed from the old drive. If anything cannot be moved, the "
          + "drive stays in the pool."
        : "Remove this drive from the pool?\n\nIts files stay on it, untouched, in its pool part folder.";

      if (!confirm(warning)) return;

      await guard(message, async () => {
        await remove(`/api/pools/${poolId}/drives/${encodeURIComponent(volumeId)}${move ? "?move=1" : ""}`);
        await refresh();
      });
    });
  });

  // Sharing a pool on the network.
  document.querySelectorAll("[data-pool]").forEach((card) => {
    const poolId = card.dataset.pool;
    const message = card.querySelector("[data-share-message]");

    const save = (body) => guard(message, async () => {
      state.shareByPool[poolId] = await post(`/api/pools/${poolId}/share`, body);
      renderPools();
    });

    card.querySelector("[data-share-enabled]")?.addEventListener("change", (event) =>
      save({ enabled: event.target.checked }));

    card.querySelector("[data-share-readonly]")?.addEventListener("change", (event) =>
      save({ readOnly: event.target.checked }));

    card.querySelector("[data-share-name]")?.addEventListener("change", (event) =>
      save({ name: event.target.value }));

    card.querySelector("[data-share-add]")?.addEventListener("click", () => {
      const input = card.querySelector("[data-share-account]");
      const account = (input?.value || "").trim();
      if (!account) return;

      const existing = state.shareByPool[poolId]?.allowedAccounts ?? [];
      save({ allowedAccounts: [...existing, account] });
    });

    card.querySelectorAll("[data-share-remove]").forEach((button) => {
      button.addEventListener("click", () => {
        const account = button.dataset.shareRemove;
        const existing = state.shareByPool[poolId]?.allowedAccounts ?? [];
        save({ allowedAccounts: existing.filter((candidate) => candidate !== account) });
      });
    });
  });
}

function wireAddDrive() {
  document.querySelectorAll("[data-add]").forEach((select) => {
    select.innerHTML = `<option value="">Add to a pool…</option>`
      + state.pools.map((pool) => `<option value="${escape(pool.poolId)}">${escape(pool.name)}</option>`).join("");

    select.addEventListener("change", async () => {
      const poolId = select.value;
      if (!poolId) return;

      await guard(el("create-pool-message"), async () => {
        await post(`/api/pools/${poolId}/drives`, { volumeIds: [select.dataset.add] });
        await refresh();
      });
    });
  });
}

function wireSettings() {
  document.querySelectorAll("[data-set]").forEach((input) => {
    const [scope, key] = input.dataset.set.split(":");

    input.addEventListener("input", async () => {
      const value = input.type === "checkbox" ? input.checked : Number(input.value);

      if (scope === "placement") {
        // Edited locally and sent on Save, so a half-moved slider never reaches the service.
        draft = { ...(draft ?? state.settings.placement), [key]: value };
        renderSettings();
        wireSettings();
        return;
      }

      // Copying and updating apply at once, as they do in the window.
      if (input.type !== "checkbox") return;
      await applyImmediate(scope, key, value);
    });

    if (input.type !== "checkbox") {
      input.addEventListener("change", async () => {
        if (scope === "placement") return;
        await applyImmediate(scope, key, Number(input.value));
      });
    }
  });

  document.querySelectorAll("[data-reset]").forEach((button) => {
    button.addEventListener("click", async () => {
      const [scope, key] = button.dataset.reset.split(":");
      const item = findItem(scope, key);
      if (!item) return;

      if (scope === "placement") {
        draft = { ...(draft ?? state.settings.placement), [key]: item.def };
        renderSettings();
        wireSettings();
        return;
      }

      await applyImmediate(scope, key, item.def);
    });
  });
}

function findItem(scope, key) {
  if (scope === "placement") {
    for (const group of PLACEMENT_GROUPS) {
      const found = group.items.find((item) => item.key === key);
      if (found) return found;
    }
    return null;
  }

  const list = scope === "duplication" ? DUPLICATION_ITEMS : UPDATE_ITEMS;
  return list.find((item) => item.key === key) ?? null;
}

async function applyImmediate(scope, key, value) {
  await guard(el("settings-message"), async () => {
    const path = scope === "duplication" ? "/api/settings/duplication" : "/api/settings/updates";
    state.settings = await post(path, { [key]: value });
    renderSettings();
    wireSettings();
  });
}

function wireProtected() {
  document.querySelectorAll("[data-protected-pool]").forEach((card) => {
    const poolId = card.dataset.protectedPool;
    const message = card.querySelector("[data-protected-message]");

    const reload = async () => {
      state.protectedByPool[poolId] = await call(`/api/pools/${poolId}/protected`);
      renderProtected();
      wireProtected();
    };

    card.querySelector("[data-protect]")?.addEventListener("click", () => guard(message, async () => {
      const folder = card.querySelector("[data-new-folder]").value.trim();
      if (!folder) throw new Error("Name a folder inside the pool first.");

      await post(`/api/pools/${poolId}/protected`, {
        folder,
        copies: Number(card.querySelector("[data-new-copies]").value) || 2,
        everyDrive: card.querySelector("[data-new-every]").checked,
        verifyByContent: card.querySelector("[data-new-verify]").checked,
        keepSurplus: card.querySelector("[data-new-keep]").checked,
      });

      await reload();
    }));

    card.querySelector("[data-protect-all]")?.addEventListener("click", () => guard(message, async () => {
      if (!confirm("Protect every file in this pool, including ones added later?")) return;

      await post(`/api/pools/${poolId}/protected`, {
        folder: "",
        copies: Number(card.querySelector("[data-new-copies]").value) || 2,
        everyDrive: card.querySelector("[data-new-every]").checked,
      });

      await reload();
    }));

    card.querySelector("[data-run-now]")?.addEventListener("click", () => guard(message, async () => {
      await post(`/api/pools/${poolId}/protected/run`);
      await reload();
    }));

    card.querySelectorAll("[data-stop]").forEach((button) => {
      button.addEventListener("click", () => guard(message, async () => {
        if (!confirm("Stop protecting this folder?\n\nNothing already on your drives is deleted.")) return;

        await remove(`/api/pools/${poolId}/protected/${button.dataset.stop}`);
        await reload();
      }));
    });
  });
}

function renderPools() {
  el("pools").innerHTML = state.pools.length === 0
    ? `<div class="card">
         <h2>No pools yet</h2>
         <p class="muted">Go to <strong>Drives</strong>, tick the drives you want, and make one.
         Nothing already on those drives is touched.</p>
       </div>`
    : state.pools.map(renderPool).join("");

  renderIoHealth();
  wirePoolButtons();
}

function showTab(next) {
  tab = next;

  document.querySelectorAll("nav.tabs button").forEach((button) => {
    button.setAttribute("aria-selected", button.dataset.tab === tab ? "true" : "false");
  });

  document.querySelectorAll("[data-panel]").forEach((panel) => {
    panel.hidden = panel.dataset.panel !== tab;
  });
}

// ---------------------------------------------------------------------------
// Loading everything
// ---------------------------------------------------------------------------

async function refresh() {
  const [status, pools, drives] = await Promise.all([
    call("/api/status"),
    call("/api/pools"),
    call("/api/drives"),
  ]);

  state.status = status;
  state.pools = pools.pools;
  state.drives = drives.drives;
  state.letters = drives.letters ?? [];

  // Everything else is per-pool or per-tab, and cheap enough to read on each refresh.
  const extras = await Promise.all([
    call("/api/settings").catch(() => null),
    call("/api/health").catch(() => null),
    call("/api/update/status").catch(() => null),
    ...state.pools.map((pool) => call(`/api/pools/${pool.poolId}/protected`).catch(() => null)),
    ...state.pools.map((pool) => call(`/api/pools/${pool.poolId}/share`).catch(() => null)),
  ]);

  state.settings = extras[0] ?? state.settings;
  state.health = extras[1];
  state.updates = extras[2];

  state.pools.forEach((pool, index) => {
    const protectedData = extras[3 + index];
    const shareData = extras[3 + state.pools.length + index];

    if (protectedData) state.protectedByPool[pool.poolId] = protectedData;
    if (shareData) state.shareByPool[pool.poolId] = shareData;
  });

  el("version").textContent = "MergePool " + status.version;
  el("subtitle").textContent = status.poolCount === 0
    ? "No pools yet — make one from the Drives tab."
    : `${status.mountedCount} of ${status.poolCount} pool(s) mounted`;

  el("read-only-note").hidden = !readOnly();

  const health = el("health");
  const problems = status.problems || [];
  const stalled = (state.health?.pools ?? []).some((pool) => pool.state === "Stalled");

  health.classList.toggle("bad", problems.length > 0 || stalled);
  el("health-text").textContent = stalled ? "Not answering"
    : problems.length === 0 ? "Healthy"
    : problems.length + " problem(s)";

  el("problems").innerHTML = problems.length === 0
    ? ""
    : `<div class="banner">${problems.map(escape).join("<br />")}</div>`;

  renderPools();
  renderDrives();
  renderSettings();
  wireSettings();
  renderProtected();
  wireProtected();
  renderUpdates();
}

function signOut() {
  setToken("");
  if (refreshTimer) clearInterval(refreshTimer);
  refreshTimer = null;
  app.hidden = true;
  signin.hidden = false;
}

async function start() {
  signin.hidden = true;
  app.hidden = false;

  try {
    await refresh();
  } catch (error) {
    if (error.unauthorized) {
      signOut();
      el("signin-error").hidden = false;
      el("signin-error").textContent = error.message;
      return;
    }

    el("problems").innerHTML = `<div class="banner">${escape(error.message)}</div>`;
  }

  if (refreshTimer) clearInterval(refreshTimer);
  refreshTimer = setInterval(() => {
    // A refresh in the background must not throw away a half-edited settings page.
    refresh().catch((error) => {
      if (error.unauthorized) signOut();
    });
  }, 5000);
}

el("signin-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  el("signin-error").hidden = true;
  setToken(el("token").value.trim());
  await start();
});

el("refresh").addEventListener("click", () => refresh().catch(() => {}));
el("signout").addEventListener("click", signOut);

document.querySelectorAll("nav.tabs button").forEach((button) => {
  button.addEventListener("click", () => showTab(button.dataset.tab));
});

el("advanced").addEventListener("change", (event) => {
  advanced = event.target.checked;
  try {
    localStorage.setItem(ADVANCED_KEY, advanced ? "1" : "0");
  } catch {
    // Private browsing: the choice then lasts for this page load only.
  }

  renderSettings();
  wireSettings();
});

el("create-pool").addEventListener("click", () => guard(el("create-pool-message"), async () => {
  const volumeIds = [...document.querySelectorAll("#new-pool-drives input:checked")].map((input) => input.value);
  if (volumeIds.length === 0) throw new Error("Tick at least one drive.");

  const result = await post("/api/pools", {
    name: el("new-pool-name").value.trim(),
    mountPoint: el("new-pool-letter").value,
    volumeIds,
    adoptExistingContent: el("new-pool-adopt").checked,
  });

  el("new-pool-adopt").checked = false;
  await refresh();
  showTab("pools");

  el("create-pool-message").textContent = `"${result.created.name}" is at ${result.created.mountPoint}.`;
}));

el("save-settings").addEventListener("click", () => guard(el("settings-message"), async () => {
  if (draft === null) return;

  state.settings = await post("/api/settings", draft);
  draft = null;
  renderSettings();
  wireSettings();
}));

el("reset-settings").addEventListener("click", () => guard(el("settings-message"), async () => {
  if (!confirm("Put every setting on this tab, the advanced ones included, back to the value MergePool ships with?")) {
    return;
  }

  const defaults = {};
  for (const group of PLACEMENT_GROUPS) {
    for (const item of group.items) defaults[item.key] = item.def;
  }

  state.settings = await post("/api/settings", defaults);
  draft = null;
  renderSettings();
  wireSettings();
}));

try {
  advanced = localStorage.getItem(ADVANCED_KEY) === "1";
  el("advanced").checked = advanced;
} catch {
  // Fine: Advanced simply starts off.
}

// A token handed over in the URL fragment, which is how MergePool opens this page for you. A
// fragment is never sent to the server, so it is not in any request, log or proxy on the way here.
// It is taken out of the address bar straight away so it is not left sitting in history.
function takeTokenFromFragment() {
  const match = /(?:^|[#&])token=([^&]+)/.exec(location.hash || "");
  if (!match) {
    return;
  }

  try {
    setToken(decodeURIComponent(match[1]));
  } catch {
    setToken(match[1]);
  }

  try {
    history.replaceState(null, "", location.pathname + location.search);
  } catch {
    location.hash = "";
  }
}

takeTokenFromFragment();

if (token()) {
  start();
} else {
  signin.hidden = false;
}
