// Animated movement for the match timeline's map. The page renders an empty overlay <svg> (with the
// map's viewBox) and the playback controls; this module draws inside the overlay and drives the
// controls, so nothing goes back to the server while it plays. Elements made here don't get the
// page's scoped CSS, so they're styled with the theme's variables directly.

const NS = "http://www.w3.org/2000/svg";
const TRAIL_MS = 3000;          // how much of each player's recent path to draw
const FLASH_MS = 1500;          // how long a kill or objective event stays on the map
const JUMP = 150;               // tenths of a square: a bigger step between samples is a respawn, not movement
const TEAM_COLOURS = ["var(--viz-1)", "var(--viz-2)"];
// A player's state with no position on the map, from the film's per-player list (PlaybackTrack.Alive).
const STATES = { 1: ["unplaced", "alive, position unknown"], 0: ["dead", "dead"], 2: ["out", "not in the match"] };
const players = new Map();      // root element → its player

export function start(root, data) {
    stop(root);
    for (const old of [...players.keys()]) if (!old.isConnected) stop(old);   // maps the page has since removed
    players.set(root, new Player(root, data));
}

export function setFocus(root, focus) {
    players.get(root)?.setFocus(focus);
}

export function stop(root) {
    players.get(root)?.dispose();
    players.delete(root);
}

function colour(team) { return TEAM_COLOURS[team] ?? "var(--muted)"; }
function el(name, attrs, parent) {
    const e = document.createElementNS(NS, name);
    for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
    parent?.appendChild(e);
    return e;
}
function clock(ms) {
    const s = Math.floor(ms / 1000);
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

class Player {
    constructor(root, data) {
        this.root = root;
        this.data = data;
        this.t = 0;
        this.speed = 1;
        this.playing = false;
        this.focus = null;
        this.reduced = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;

        this.svg = root.querySelector("svg.overlay");
        this.button = root.querySelector("button.play");
        this.scrub = root.querySelector("input.scrub");
        this.clock = root.querySelector(".clock");
        this.speedSelect = root.querySelector("select.speed");

        this.roster = new Map([...root.querySelectorAll(".roster li")].map(li => [Number(li.dataset.player), { li, state: li.querySelector(".state") }]));
        this.events = el("g", {}, this.svg);
        this.marks = data.players.map(p => {
            const g = el("g", {}, this.svg);
            const c = colour(p.team);
            const trail = el("path", { fill: "none", stroke: c, "stroke-width": ".45", "stroke-opacity": ".45", "stroke-linecap": "round", "stroke-linejoin": "round" }, g);
            const facing = el("line", { stroke: c, "stroke-width": ".5", "stroke-linecap": "round" }, g);
            const dot = el("circle", { r: "1.3", fill: c, stroke: "var(--surface)", "stroke-width": ".3" }, g);
            el("title", {}, dot).textContent = p.name;
            return { g, trail, facing, dot };
        });

        this.onButton = () => this.playing ? this.pause() : this.play();
        this.onScrub = () => { this.t = Number(this.scrub.value); this.render(); };
        this.onSpeed = () => { this.speed = Number(this.speedSelect.value) || 1; };
        this.button.addEventListener("click", this.onButton);
        this.scrub.addEventListener("input", this.onScrub);
        this.speedSelect.addEventListener("change", this.onSpeed);
        this.onSpeed();
        this.render();
    }

    play() {
        if (this.t >= this.data.durationMs) this.t = 0;
        this.playing = true;
        this.button.textContent = "Pause";
        this.button.setAttribute("aria-pressed", "true");
        let last = performance.now();
        const step = now => {
            if (!this.playing) return;
            if (!this.root.isConnected) { stop(this.root); return; }
            this.t = Math.min(this.data.durationMs, this.t + (now - last) * this.speed);
            last = now;
            this.render();
            if (this.t >= this.data.durationMs) { this.pause(); return; }
            this.frame = requestAnimationFrame(step);
        };
        this.frame = requestAnimationFrame(step);
    }

    pause() {
        this.playing = false;
        cancelAnimationFrame(this.frame);
        this.button.textContent = "Play";
        this.button.setAttribute("aria-pressed", "false");
    }

    setFocus(focus) {
        this.focus = focus && focus.length ? new Set(focus) : null;
        this.render();
    }

    dispose() {
        this.pause();
        this.button.removeEventListener("click", this.onButton);
        this.scrub.removeEventListener("input", this.onScrub);
        this.speedSelect.removeEventListener("change", this.onSpeed);
        this.svg.replaceChildren();
    }

    // A player's position (in squares) and facing at a time, interpolated between samples, or null.
    at(p, ms) {
        const f = ms / this.data.stepMs, i = Math.floor(f), a = f - i;
        const x0 = p.x[i], y0 = p.y[i];
        if (x0 === undefined || x0 < 0) return null;
        let x = x0, y = y0;
        const x1 = p.x[i + 1], y1 = p.y[i + 1];
        if (x1 !== undefined && x1 >= 0 && Math.abs(x1 - x0) + Math.abs(y1 - y0) < JUMP) { x += (x1 - x0) * a; y += (y1 - y0) * a; }
        const yaw = p.yaw[i];
        return { x: x / 10, y: y / 10, yaw: yaw >= 0 ? yaw : null };
    }

    trail(p, ms) {
        const step = this.data.stepMs;
        let d = "", pen = false, prev = null;
        for (let f = Math.max(0, Math.ceil((ms - TRAIL_MS) / step)); f * step <= ms; f++) {
            const x = p.x[f], y = p.y[f];
            if (x === undefined || x < 0 || (prev && Math.abs(x - prev.x) + Math.abs(y - prev.y) >= JUMP)) { pen = false; prev = null; continue; }
            d += `${pen ? "L" : "M"}${x / 10} ${y / 10}`;
            pen = true; prev = { x, y };
        }
        const now = this.at(p, ms);
        if (pen && now) d += `L${now.x} ${now.y}`;
        return d;
    }

    render() {
        const t = this.t, data = this.data;
        this.scrub.value = String(Math.round(t));
        this.clock.textContent = `${clock(t)} / ${clock(data.durationMs)}`;

        const frame = Math.floor(t / data.stepMs);
        data.players.forEach((p, k) => {
            const m = this.marks[k], pos = this.at(p, t), inFocus = !this.focus || this.focus.has(p.player);
            m.g.setAttribute("opacity", inFocus ? "1" : ".2");
            const [state, text] = pos ? ["alive", "alive"] : STATES[p.alive[frame]] ?? ["unknown", "—"];
            const row = this.roster.get(p.player);
            if (row) {
                row.li.dataset.state = state;
                row.li.hidden = !inFocus;
                if (row.state.textContent !== text) row.state.textContent = text;
            }
            if (!pos) { m.g.setAttribute("visibility", "hidden"); return; }
            m.g.setAttribute("visibility", "visible");
            m.dot.setAttribute("cx", pos.x); m.dot.setAttribute("cy", pos.y);
            if (pos.yaw === null) m.facing.setAttribute("visibility", "hidden");
            else {
                const r = pos.yaw * Math.PI / 180;
                m.facing.setAttribute("visibility", "visible");
                m.facing.setAttribute("x1", pos.x); m.facing.setAttribute("y1", pos.y);
                m.facing.setAttribute("x2", pos.x + 3.2 * Math.cos(r)); m.facing.setAttribute("y2", pos.y - 3.2 * Math.sin(r));
            }
            m.trail.setAttribute("d", this.reduced ? "" : this.trail(p, t));
        });

        // Kills and objective events from the last moments, fading out (shown without fading for reduced motion).
        this.events.replaceChildren();
        const fade = e => this.reduced ? 1 : 1 - (t - e.t) / FLASH_MS;
        const shown = e => e.t <= t && t - e.t < FLASH_MS && (!this.focus || this.focus.has(e.player) || this.focus.has(e.other));
        for (const k of data.kills) {
            if (!shown(k)) continue;
            const c = colour(k.team), o = fade(k);
            if (k.kx >= 0) el("line", { x1: k.kx / 10, y1: k.ky / 10, x2: k.vx / 10, y2: k.vy / 10, stroke: c, "stroke-width": ".4", opacity: o }, this.events);
            const x = k.vx / 10, y = k.vy / 10;
            el("path", { d: `M${x - 1.2} ${y - 1.2}L${x + 1.2} ${y + 1.2}M${x + 1.2} ${y - 1.2}L${x - 1.2} ${y + 1.2}`, stroke: c, "stroke-width": ".5", opacity: o }, this.events);
        }
        for (const e of data.objectives) {
            if (!shown(e)) continue;
            const x = e.x / 10, y = e.y / 10;
            el("rect", { x: x - 1, y: y - 1, width: 2, height: 2, transform: `rotate(45 ${x} ${y})`, fill: colour(e.team), opacity: fade(e) }, this.events);
        }
    }
}
