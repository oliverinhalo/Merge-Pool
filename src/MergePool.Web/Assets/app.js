// The whole web interface. It holds the access token in sessionStorage — closing the tab signs you
// out — and sends it on every call, because the server keeps no session of its own.

const TOKEN_KEY = "mergepool.token";

const el = (id) => document.getElementById(id);
const signin = el("signin");
const app = el("app");

let refreshTimer = null;

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

function driveStatus(drive) {
  if (drive.state === "Offline") return { text: "Missing", css: "state-Missing" };
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
  return (drive.throughputBytesPerSecond / (1024 * 1024)).toFixed(1) + " MB/s";
}

function renderPool(pool) {
  // The two figures the desktop window separates: what the pool holds, and what its drives hold.
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
        </div>
      </div>`;
  }).join("");

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

      <div class="actions">
        <button data-act="mount" ${pool.mounted ? "disabled" : ""}>Mount</button>
        <button data-act="unmount" ${pool.mounted ? "" : "disabled"}>Unmount</button>
        <button data-act="rebalance">Rebalance</button>
      </div>
    </section>`;
}

function renderFreeDrives(drives) {
  const free = drives.filter((drive) => drive.poolable && !drive.poolId);

  if (free.length === 0) {
    return `<p class="muted">Every drive that can be pooled already is.</p>`;
  }

  return free.map((drive) => `
    <div class="row">
      <div class="letter">${escape(drive.letter ? drive.letter + ":" : "—")}</div>
      <div class="body">
        <div class="name">${escape(drive.label || "(no label)")}</div>
        <div class="muted">${bytes(drive.freeBytes)} free of ${bytes(drive.totalBytes)} · ${escape(drive.fileSystem)}</div>
      </div>
      <div class="side">
        <select data-add="${escape(drive.volumeId)}" aria-label="Add ${escape(drive.label)} to a pool"></select>
      </div>
    </div>`).join("");
}

async function refresh() {
  const [status, pools, drives] = await Promise.all([
    call("/api/status"),
    call("/api/pools"),
    call("/api/drives"),
  ]);

  el("version").textContent = "MergePool " + status.version;
  el("subtitle").textContent = status.poolCount === 0
    ? "No pools yet — create one in the app on the computer running MergePool."
    : `${status.mountedCount} of ${status.poolCount} pool(s) mounted`;

  const health = el("health");
  const problems = status.problems || [];
  health.className = problems.length > 0 ? "pill bad" : "pill";
  el("health-text").textContent = problems.length > 0 ? "Needs attention" : "Healthy";

  el("problems").innerHTML = problems
    .map((problem) => `<div class="banner warn">${escape(problem)}</div>`)
    .join("");

  el("pools").innerHTML = (pools.pools || []).map(renderPool).join("");
  el("free-drives").innerHTML = renderFreeDrives(drives.drives || []);

  wirePoolButtons(pools.pools || []);
  wireAddDrive(pools.pools || []);
}

function wirePoolButtons(pools) {
  document.querySelectorAll("[data-pool] [data-act]").forEach((button) => {
    button.addEventListener("click", async () => {
      const poolId = button.closest("[data-pool]").dataset.pool;
      const action = button.dataset.act;
      const pool = pools.find((candidate) => candidate.poolId === poolId);

      if (action === "rebalance"
        && !confirm(`Even out the drives in '${pool ? pool.name : "this pool"}'? Files in use are skipped.`)) {
        return;
      }

      await run(button, () => call(`/api/pools/${poolId}/${action}`, { method: "POST" }));
    });
  });
}

function wireAddDrive(pools) {
  document.querySelectorAll("[data-add]").forEach((select) => {
    select.innerHTML = `<option value="">Add to…</option>`
      + pools.map((pool) => `<option value="${escape(pool.poolId)}">${escape(pool.name)}</option>`).join("");

    select.addEventListener("change", async () => {
      const poolId = select.value;
      if (!poolId) return;

      const pool = pools.find((candidate) => candidate.poolId === poolId);
      if (!confirm(`Add this drive to '${pool ? pool.name : "the pool"}'?\n\n`
        + "Nothing already on the drive is moved or deleted — MergePool only adds its own folder.")) {
        select.value = "";
        return;
      }

      await run(select, () => call(`/api/pools/${poolId}/drives`, {
        method: "POST",
        body: JSON.stringify({ volumeIds: [select.dataset.add] }),
      }));
    });
  });
}

async function run(control, action) {
  control.disabled = true;
  try {
    await action();
    await refresh();
  } catch (error) {
    if (error.unauthorized) {
      showSignin("The access token is no longer accepted. It may have been changed.");
      return;
    }
    alert(error.message);
  } finally {
    control.disabled = false;
  }
}

function showSignin(message) {
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }

  app.hidden = true;
  signin.hidden = false;

  const error = el("signin-error");
  error.hidden = !message;
  error.textContent = message || "";
}

async function showApp() {
  signin.hidden = true;
  app.hidden = false;

  await refresh();

  if (!refreshTimer) {
    refreshTimer = setInterval(() => refresh().catch((error) => {
      if (error.unauthorized) showSignin("The access token is no longer accepted.");
    }), 5000);
  }
}

el("signin-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  setToken(el("token").value.trim());

  try {
    await showApp();
    el("token").value = "";
  } catch (error) {
    setToken("");
    showSignin(error.unauthorized ? "That token was not accepted." : error.message);
  }
});

el("refresh").addEventListener("click", () => refresh().catch((error) => alert(error.message)));

el("signout").addEventListener("click", () => {
  setToken("");
  showSignin("");
});

// Straight in if the token from a previous visit still works.
(async () => {
  if (!token()) {
    showSignin("");
    return;
  }

  try {
    await showApp();
  } catch (error) {
    showSignin(error.unauthorized ? "" : error.message);
  }
})();
