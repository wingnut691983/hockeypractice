/* ============================================================
   Drill drawing editor.

   Loaded lazily, the first time a coach presses "Draw a diagram", so players and phones never
   download it. Its whole interface is HPSketch.open(options): it takes an optional drawing (the
   JSON) and hands back a PNG and the JSON through options.onUse, or nothing on cancel.

   It knows nothing about drills on purpose: no ids, no form fields, no URLs. Only the page that
   opens it knows where the result goes. That is what lets another page (a plan's ice layout,
   say) use the same editor later without a rewrite.

   Safety rule: every piece of text a drawing carries (labels, text items) is set with
   textContent, never innerHTML. A drill copied from another team carries that team's text.

   Model: { v: 1, rink: "full|fullh|half|zone", items: [...] }, coordinates in feet on a
   200 x 85 rink with x along its length. Each item is one of
     { t: "path", style, end, color, pts: [[x,y],...], str? }
     { t: "mark", kind, x, y, rot, color, label? }
     { t: "text", x, y, s, color }
     { t: "num",  x, y, n, color }
   The geometry below is kept in pure functions so it can be tested without a browser later.
   ============================================================ */
(function () {
    'use strict';
    if (window.HPSketch) return;

    var NS = 'http://www.w3.org/2000/svg';
    var LEN = 200, WID = 85;
    var FONT = 'Arial, Helvetica, sans-serif';
    var MAX_ITEMS = 400, MAX_POINTS = 400;

    // ── Rink views ──────────────────────────────────────────────────────
    // One drawing, several windows onto it. Vertical views put a net at the top, which is what
    // reads on a phone held upright: a sideways full rink is about 150px tall at 360px wide.
    var VIEWS = {
        full:  { label: 'Full rink',           vertical: true,  x0: 0,   x1: 200 },
        fullh: { label: 'Full rink, sideways', vertical: false, x0: 0,   x1: 200 },
        half:  { label: 'Half rink',           vertical: true,  x0: 100, x1: 200 },
        zone:  { label: 'One zone',            vertical: true,  x0: 125, x1: 200 }
    };
    var VIEW_ORDER = ['full', 'fullh', 'half', 'zone'];

    // Six fixed inks, chosen to read on the white rink and on paper. Orange is kept well away
    // from red. Colour only ever groups: line style and labels carry the meaning, because red
    // and green look alike to roughly 1 in 12 boys.
    var INKS = {
        black:  '#16191d',
        red:    '#d32f2f',
        orange: '#ef7c00',
        green:  '#2e7d32',
        blue:   '#1565c0',
        purple: '#7b1fa2'
    };
    var INK_ORDER = ['black', 'red', 'orange', 'green', 'blue', 'purple'];

    var PATHS = {
        forward:   { label: 'Skate',           end: 'arrow' },
        backward:  { label: 'Skate backward',  end: 'arrow' },
        puck:      { label: 'Skate with puck', end: 'arrow' },
        pass:      { label: 'Pass',            end: 'arrow' },
        shot:      { label: 'Shot',            end: 'arrow' },
        sidestep:  { label: 'Sidestep',        end: 'none' },
        crossover: { label: 'Crossovers',      end: 'none' }
    };
    var PATH_ORDER = ['forward', 'backward', 'puck', 'pass', 'shot', 'sidestep', 'crossover'];
    var ENDS = { arrow: 'Arrow', stop: 'Stop', none: 'None' };

    // ── Stamps, drawn in a 10-unit box centred on 0,0 ───────────────────
    function letter(ch) {
        return function (g, ink) {
            el('text', {
                x: 0, y: 3.4, 'font-size': 9.6, 'font-weight': 700, 'font-family': FONT,
                'text-anchor': 'middle', fill: ink
            }, g).textContent = ch;
        };
    }
    function stroked(d, w) {
        return function (g, ink) {
            el('path', {
                d: d, fill: 'none', stroke: ink, 'stroke-width': w,
                'stroke-linecap': 'round', 'stroke-linejoin': 'round'
            }, g);
        };
    }
    // Feet per glyph unit. Stamps are drawn in a 10-unit box, so a letter comes out about 2.7 ft
    // tall. Blockers are the exception that is drawn to scale, so they convert through this.
    var GLYPH_FT = 0.4;

    // A padded blocker, drawn at its real size: item.len feet long and 1 ft wide, lying across the
    // view until rotated. The only stamp that is not a symbol, because where it sits on the ice and
    // how much of the lane it closes is the whole point of drawing one. One stamp with a length
    // rather than a stamp per size: the coach asked for it to be stretched on the rink, since real
    // blockers come in 6 and 10 ft and lining several up end to end is common too.
    var BLOCKER_DEFAULT = 6, BLOCKER_MIN = 2, BLOCKER_MAX = 40;
    function drawBlocker(g, ink, item) {
        var len = item && item.len ? item.len : BLOCKER_DEFAULT;
        var u = len / GLYPH_FT, w = 1 / GLYPH_FT;
        el('rect', {
            x: f2(-u / 2), y: f2(-w / 2), width: f2(u), height: f2(w), rx: f2(w / 3),
            fill: ink, 'fill-opacity': 0.3, stroke: ink, 'stroke-width': 0.6
        }, g);
    }

    var MARKS = {
        X:     { label: 'Player',     draw: stroked('M-3.3,-3.3L3.3,3.3M3.3,-3.3L-3.3,3.3', 1.3) },
        O:     { label: 'Opponent',   draw: function (g, ink) {
                     el('circle', { r: 3.4, fill: 'none', stroke: ink, 'stroke-width': 1.2 }, g); } },
        F:     { label: 'Forward',    draw: letter('F') },
        C:     { label: 'Center',     draw: letter('C') },
        W:     { label: 'Winger',     draw: letter('W') },
        D:     { label: 'Defense',    draw: letter('D') },
        G:     { label: 'Goalie',     draw: letter('G') },
        coach: { label: 'Coach',      draw: function (g, ink) {
                     el('circle', { r: 4.6, fill: 'none', stroke: ink, 'stroke-width': 0.9 }, g);
                     el('text', { x: 0, y: 2.4, 'font-size': 6.8, 'font-weight': 700, 'font-family': FONT,
                                  'text-anchor': 'middle', fill: ink }, g).textContent = 'C'; } },
        puck:  { label: 'Puck',       draw: function (g, ink) { el('circle', { r: 1.35, fill: ink }, g); } },
        stick: { label: 'Stick', rot: true, draw: stroked('M-4.6,0.6L-2.1,3.6L4.8,-4.4', 1.3) },
        cone:  { label: 'Cone',       draw: function (g, ink) {
                     el('path', { d: 'M0,-3.3L3,2.7L-3,2.7Z', fill: ink, stroke: ink,
                                  'stroke-width': 0.4, 'stroke-linejoin': 'round' }, g); } },
        net:   { label: 'Net',   rot: true, draw: stroked('M-5,-2.2L5,-2.2M-3.6,-2.2C-3.6,3.8 3.6,3.8 3.6,-2.2', 1.1) },
        pivot: { label: 'Pivot', rot: true,
                 draw: stroked('M-3.6,3.8C-1.5,2.8 2.2,-0.6 1.6,-3C1.2,-4.6 -1,-4 -0.7,-1.8C-0.4,0.6 2,2.6 4.2,3.2', 1) },
        turn:  { label: 'Turn',  rot: true,
                 draw: stroked('M-4.5,2.8C-4.5,-3.6 3.4,-3.8 3.4,1.6M1.4,-0.4L3.4,2.6L5.3,-0.4', 1) },
        stop:  { label: 'Stop',  rot: true, draw: stroked('M-1.3,-3.2L-1.3,3.2M1.3,-3.2L1.3,3.2', 1.2) },
        // Not in the legend; coaches use them constantly. Real length, so the icon is drawn apart.
        blocker: { label: 'Blocker', rot: true, scaled: true, draw: drawBlocker }
    };
    var MARK_ORDER = ['X', 'O', 'F', 'C', 'W', 'D', 'G', 'coach', 'puck', 'stick', 'cone', 'net',
                      'pivot', 'turn', 'stop', 'blocker'];

    // ── Small helpers ───────────────────────────────────────────────────
    function el(name, attrs, parent) {
        var e = document.createElementNS(NS, name);
        for (var k in attrs) if (attrs[k] !== null && attrs[k] !== undefined) e.setAttribute(k, attrs[k]);
        if (parent) parent.appendChild(e);
        return e;
    }
    function h(tag, attrs, parent, text) {
        var e = document.createElement(tag);
        for (var k in attrs) {
            if (attrs[k] === null || attrs[k] === undefined || attrs[k] === false) continue;
            if (k === 'className') e.className = attrs[k];
            else e.setAttribute(k, attrs[k] === true ? '' : attrs[k]);
        }
        if (text !== undefined) e.textContent = text;
        if (parent) parent.appendChild(e);
        return e;
    }
    function r1(n) { return Math.round(n * 10) / 10; }
    function f2(n) { return String(Math.round(n * 100) / 100); }
    function num(v, lo, hi, dflt) {
        return typeof v === 'number' && isFinite(v) ? Math.min(hi, Math.max(lo, v)) : dflt;
    }
    function dist(a, b) { return Math.sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])); }
    function pd(points) {
        var s = '';
        for (var i = 0; i < points.length; i++)
            s += (i ? 'L' : 'M') + f2(points[i][0]) + ',' + f2(points[i][1]);
        return s;
    }

    // ── Geometry (pure) ─────────────────────────────────────────────────
    // Rink feet to the view's own coordinates, and back. Vertical views turn the rink a quarter
    // so x runs up the screen: view = (y, 200 - x).
    function toView(view, p) { return VIEWS[view].vertical ? [p[1], LEN - p[0]] : [p[0], p[1]]; }
    function toRink(view, v) { return VIEWS[view].vertical ? [LEN - v[1], v[0]] : [v[0], v[1]]; }
    function deltaToRink(view, d) { return VIEWS[view].vertical ? [-d[1], d[0]] : [d[0], d[1]]; }
    function viewRect(view) {
        var V = VIEWS[view];
        return V.vertical
            ? { x: 0, y: LEN - V.x1, w: WID, h: V.x1 - V.x0 }
            : { x: V.x0, y: 0, w: V.x1 - V.x0, h: WID };
    }
    // What is DRAWN is sized in rink feet, the same in every view, so a sideways rink and an
    // upright one show a player the same size relative to the ice. The first version scaled
    // drawings with the view's width, which made the sideways view draw everything 2.35x too big;
    // the coach compared it against a published drill and every line and letter was about twice
    // the size it should be. scaleOf is now used only for touch and selection tolerances (how
    // near a tap must land, the selection box, handles), which are about fingers, not the picture.
    function scaleOf(view) { return viewRect(view).w / WID; }
    function turnOf(view) { return VIEWS[view].vertical ? -90 : 0; }

    function inside(view, p, slack) {
        var r = viewRect(view), v = toView(view, p);
        return v[0] >= r.x - slack && v[0] <= r.x + r.w + slack && v[1] >= r.y - slack && v[1] <= r.y + r.h + slack;
    }

    // Ramer-Douglas-Peucker: drops the jitter of a hand-drawn stroke and keeps its shape.
    function simplify(pts, eps) {
        if (pts.length < 3) return pts.slice();
        var keep = new Array(pts.length), stack = [[0, pts.length - 1]];
        keep[0] = keep[pts.length - 1] = true;
        while (stack.length) {
            var seg = stack.pop(), a = pts[seg[0]], b = pts[seg[1]], best = -1, idx = -1;
            var dx = b[0] - a[0], dy = b[1] - a[1], len = Math.sqrt(dx * dx + dy * dy);
            for (var i = seg[0] + 1; i < seg[1]; i++) {
                var dd = len < 1e-9 ? dist(pts[i], a)
                    : Math.abs(dy * pts[i][0] - dx * pts[i][1] + b[0] * a[1] - b[1] * a[0]) / len;
                if (dd > best) { best = dd; idx = i; }
            }
            if (best > eps) {
                keep[idx] = true;
                stack.push([seg[0], idx], [idx, seg[1]]);
            }
        }
        return pts.filter(function (_, i) { return keep[i]; });
    }

    // Catmull-Rom through the kept points, so a freehand line and a tapped one come out as the
    // same smooth curve.
    function catmull(pts) {
        if (pts.length < 3) return pts.slice();
        var out = [pts[0]];
        for (var i = 0; i < pts.length - 1; i++) {
            var p0 = pts[i - 1] || pts[i], p1 = pts[i], p2 = pts[i + 1], p3 = pts[i + 2] || p2;
            var n = Math.max(2, Math.ceil(dist(p1, p2) / 0.5));
            for (var j = 1; j <= n; j++) {
                var t = j / n, t2 = t * t, t3 = t2 * t;
                out.push([0, 1].map(function (c) {
                    return 0.5 * (2 * p1[c] + (p2[c] - p0[c]) * t
                        + (2 * p0[c] - 5 * p1[c] + 4 * p2[c] - p3[c]) * t2
                        + (3 * p1[c] - p0[c] - 3 * p2[c] + p3[c]) * t3);
                }));
            }
        }
        return out;
    }

    function measure(poly) {
        var cum = [0];
        for (var i = 1; i < poly.length; i++) cum.push(cum[i - 1] + dist(poly[i - 1], poly[i]));
        return { p: poly, c: cum, L: cum[cum.length - 1] };
    }

    // Point and unit tangent at arc length s.
    function at(m, s) {
        s = Math.max(0, Math.min(m.L, s));
        var i = 1;
        while (i < m.c.length - 1 && m.c[i] < s) i++;
        var a = m.p[i - 1], b = m.p[i], seg = m.c[i] - m.c[i - 1];
        var t = seg > 1e-9 ? (s - m.c[i - 1]) / seg : 0;
        var dx = b[0] - a[0], dy = b[1] - a[1], len = Math.sqrt(dx * dx + dy * dy);
        if (len < 1e-9) {
            // A repeated point has no direction of its own; borrow the overall one.
            var f = m.p[0], l = m.p[m.p.length - 1];
            dx = l[0] - f[0]; dy = l[1] - f[1]; len = Math.sqrt(dx * dx + dy * dy) || 1;
        }
        return { x: a[0] + (b[0] - a[0]) * t, y: a[1] + (b[1] - a[1]) * t, tx: dx / len, ty: dy / len };
    }

    // The polyline up to arc length s1, keeping its own vertices.
    function cut(m, s1) {
        var out = [];
        for (var i = 0; i < m.p.length && m.c[i] < s1; i++) out.push(m.p[i]);
        var q = at(m, s1);
        out.push([q.x, q.y]);
        return out;
    }

    // Samples along the curve with a sideways offset, which is how every wavy style is made.
    function offsetRun(m, s1, step, fn) {
        var out = [];
        for (var s = 0; s < s1; s += step) {
            var q = at(m, s), o = fn(s);
            out.push([q.x - q.ty * o, q.y + q.tx * o]);
        }
        var e = at(m, s1), oe = fn(s1);
        out.push([e.x - e.ty * oe, e.y + e.tx * oe]);
        return out;
    }

    // Every shape a path style draws, in view units, as { d, w, fill?, dash? }. k scales weights.
    // The legend is the reference for each: see docs/drill-legend.md.
    function pathShapes(style, end, poly, k) {
        var out = [], m = measure(poly);
        if (m.L < 0.05) return out;

        // Measured off a published drill the coach supplied as the target: lines about 0.3 ft,
        // arrowheads about 1.7 ft, a puck-carry wave about 0.75 ft either side.
        var lw = 0.3 * k, head = 1.7 * k, headW = 1.4 * k;
        var arrow = end === 'arrow';
        // Lines stop short of an arrowhead so no stroke pokes through its tip.
        var s1 = Math.max(0, m.L - (arrow ? head * 0.8 : 0));

        switch (style) {
            case 'pass':
                out.push({ d: pd(cut(m, s1)), w: lw, dash: [1.3 * k, 0.9 * k] });
                break;
            case 'puck': {
                // A whole number of half-waves, so the wave lands on the line at the arrow.
                var P = s1 / Math.max(1, Math.round(s1 / (1.25 * k))), A = 0.75 * k;
                out.push({ d: pd(offsetRun(m, s1, 0.15 * k, function (s) {
                    return A * Math.sin(Math.PI * s / P);
                })), w: lw });
                break;
            }
            case 'backward': {
                // Shallow cups joined end to end, all on one side of the path.
                var C = s1 / Math.max(1, Math.round(s1 / (2.6 * k))), D = 0.65 * k;
                out.push({ d: pd(offsetRun(m, s1, 0.12 * k, function (s) {
                    return D * Math.abs(Math.sin(Math.PI * s / C));
                })), w: lw });
                break;
            }
            case 'shot': {
                // Two parallel lines into one open, hollow arrowhead, like =>.
                var off = 0.42 * k, sEnd = arrow ? m.L - head * 0.75 : m.L;
                [off, -off].forEach(function (o) {
                    out.push({ d: pd(offsetRun(m, Math.max(0, sEnd), 0.4 * k, function () { return o; })), w: lw * 0.85 });
                });
                if (arrow) {
                    var tip = at(m, m.L), base = at(m, m.L - head * 1.25), wing = off + headW * 0.6;
                    out.push({ d: pd([
                        [base.x - base.ty * wing, base.y + base.tx * wing],
                        [tip.x, tip.y],
                        [base.x + base.ty * wing, base.y - base.tx * wing]
                    ]), w: lw * 0.85 });
                }
                break;
            }
            case 'sidestep': {
                // Short ticks across the path, with no line through them.
                var gap = 0.95 * k, half = 0.7 * k, d = '';
                for (var s = gap / 2; s <= s1 + 1e-6; s += gap) {
                    var q = at(m, s);
                    d += pd([[q.x - q.ty * half, q.y + q.tx * half], [q.x + q.ty * half, q.y - q.tx * half]]);
                }
                if (d) out.push({ d: d, w: lw * 0.8 });
                break;
            }
            case 'crossover': {
                // A row of small X marks, with nothing joining them.
                var gx = 1.9 * k, hx = 0.5 * k, dx = '';
                for (var sx = gx / 2; sx <= s1 + 1e-6; sx += gx) {
                    var qx = at(m, sx), ax = (qx.tx - qx.ty) * hx, ay = (qx.ty + qx.tx) * hx,
                        bx = (qx.tx + qx.ty) * hx, by = (qx.ty - qx.tx) * hx;
                    dx += pd([[qx.x - ax, qx.y - ay], [qx.x + ax, qx.y + ay]])
                        + pd([[qx.x - bx, qx.y - by], [qx.x + bx, qx.y + by]]);
                }
                if (dx) out.push({ d: dx, w: lw * 0.8 });
                break;
            }
            default:
                out.push({ d: pd(cut(m, s1)), w: lw });
        }

        var e = at(m, m.L);
        if (arrow && style !== 'shot') {
            var bx2 = e.x - e.tx * head, by2 = e.y - e.ty * head, hw = headW / 2;
            out.push({ d: pd([[e.x, e.y], [bx2 - e.ty * hw, by2 + e.tx * hw], [bx2 + e.ty * hw, by2 - e.tx * hw]]) + 'Z', fill: true });
        } else if (end === 'stop') {
            var bar = 0.9 * k, sd = '';
            [0, 0.5 * k].forEach(function (back) {
                var q2 = at(m, m.L - back);
                sd += pd([[q2.x - q2.ty * bar, q2.y + q2.tx * bar], [q2.x + q2.ty * bar, q2.y - q2.tx * bar]]);
            });
            out.push({ d: sd, w: lw });
        }
        return out;
    }

    // ── Model hygiene ───────────────────────────────────────────────────
    // A drawing can arrive from another team (a copied drill) or from storage, so every field is
    // re-checked and anything unexpected is dropped or defaulted rather than trusted.
    function cleanModel(raw) {
        raw = raw && typeof raw === 'object' ? raw : {};
        var model = { v: 1, rink: VIEWS[raw.rink] ? raw.rink : 'full', items: [] };
        var items = Array.isArray(raw.items) ? raw.items.slice(0, MAX_ITEMS) : [];
        items.forEach(function (it) {
            var c = cleanItem(it);
            if (c) model.items.push(c);
        });
        return model;
    }
    function cleanItem(it) {
        if (!it || typeof it !== 'object') return null;
        var color = INKS.hasOwnProperty(it.color) ? it.color : 'black';
        var x = num(it.x, -50, 250, null), y = num(it.y, -50, 150, null);
        switch (it.t) {
            case 'path': {
                if (!PATHS.hasOwnProperty(it.style) || !Array.isArray(it.pts)) return null;
                var pts = it.pts.slice(0, MAX_POINTS).filter(function (p) {
                    return Array.isArray(p) && num(p[0], -50, 250, null) !== null && num(p[1], -50, 150, null) !== null;
                }).map(function (p) { return [r1(p[0]), r1(p[1])]; });
                if (pts.length < 2) return null;
                var out = { t: 'path', style: it.style, end: ENDS.hasOwnProperty(it.end) ? it.end : PATHS[it.style].end, color: color, pts: pts };
                if (it.str) out.str = 1;
                return out;
            }
            case 'mark': {
                if (!MARKS.hasOwnProperty(it.kind) || x === null || y === null) return null;
                var m = { t: 'mark', kind: it.kind, x: x, y: y, rot: num(it.rot, -3600, 3600, 0) % 360, color: color };
                if (it.kind === 'blocker') m.len = Math.round(num(it.len, BLOCKER_MIN, BLOCKER_MAX, BLOCKER_DEFAULT));
                if (typeof it.label === 'string' && it.label.trim()) m.label = it.label.trim().slice(0, 2);
                return m;
            }
            case 'text':
                if (typeof it.s !== 'string' || !it.s.trim() || x === null || y === null) return null;
                return { t: 'text', x: x, y: y, s: it.s.trim().slice(0, 40), color: color };
            case 'num':
                if (x === null || y === null) return null;
                return { t: 'num', x: x, y: y, n: Math.round(num(it.n, 1, 99, 1)), color: color };
        }
        return null;
    }
    function parse(json) {
        if (!json) return null;
        try { return cleanModel(JSON.parse(json)); } catch (e) { return null; }
    }
    function serialize(model) { return JSON.stringify({ v: 1, rink: model.rink, items: model.items }); }

    // ── Drawing the scene ───────────────────────────────────────────────
    var clipSeq = 0;
    var RED = '#efa7a7', BLUE = '#a7bfe6', BOARDS = '#7d8794', CREASE = '#e4eefb';

    function drawRink(parent, view) {
        var id = 'hps-clip-' + (++clipSeq);
        var V = VIEWS[view];
        var g = el('g', { transform: V.vertical ? 'matrix(0 -1 1 0 0 ' + LEN + ')' : null }, parent);
        var defs = el('defs', {}, g);
        el('rect', { x: 0, y: 0, width: LEN, height: WID, rx: 28, ry: 28 }, el('clipPath', { id: id }, defs));
        el('rect', { x: 0, y: 0, width: LEN, height: WID, rx: 28, ry: 28, fill: '#fff' }, g);

        var c = el('g', { 'clip-path': 'url(#' + id + ')' }, g);
        var thin = 0.3;
        el('rect', { x: 11 - thin / 2, y: 0, width: thin, height: WID, fill: RED }, c);
        el('rect', { x: 189 - thin / 2, y: 0, width: thin, height: WID, fill: RED }, c);
        el('rect', { x: 74.5, y: 0, width: 1, height: WID, fill: BLUE }, c);
        el('rect', { x: 124.5, y: 0, width: 1, height: WID, fill: BLUE }, c);
        el('rect', { x: 99.5, y: 0, width: 1, height: WID, fill: RED }, c);
        el('circle', { cx: 100, cy: 42.5, r: 15, fill: 'none', stroke: BLUE, 'stroke-width': thin }, c);
        el('circle', { cx: 100, cy: 42.5, r: 0.6, fill: BLUE }, c);

        [31, 169].forEach(function (cx) {
            [20.5, 64.5].forEach(function (cy) {
                el('circle', { cx: cx, cy: cy, r: 15, fill: 'none', stroke: RED, 'stroke-width': thin }, c);
                el('circle', { cx: cx, cy: cy, r: 1, fill: RED }, c);
                var hm = '';
                [-1, 1].forEach(function (sy) {
                    [-2.8, 2.8].forEach(function (dx) {
                        var y0 = cy + sy * Math.sqrt(225 - dx * dx);
                        hm += 'M' + (cx + dx) + ',' + f2(y0) + 'L' + (cx + dx) + ',' + f2(y0 + sy * 2);
                    });
                });
                el('path', { d: hm, stroke: RED, 'stroke-width': thin, fill: 'none' }, c);
            });
        });
        [80, 120].forEach(function (cx) {
            [20.5, 64.5].forEach(function (cy) { el('circle', { cx: cx, cy: cy, r: 1, fill: RED }, c); });
        });

        // NHL crease: a 6 ft arc cut by straight sides 4 ft either side of centre, so 8 ft wide,
        // not the 12 ft a plain half circle gives. 4.47 is sqrt(6^2 - 4^2), where side meets arc.
        el('path', { d: 'M11,38.5L15.47,38.5A6,6 0 0 1 15.47,46.5L11,46.5Z', fill: CREASE, stroke: RED, 'stroke-width': thin }, c);
        el('path', { d: 'M189,38.5L184.53,38.5A6,6 0 0 0 184.53,46.5L189,46.5Z', fill: CREASE, stroke: RED, 'stroke-width': thin }, c);
        el('rect', { x: 7.7, y: 39.5, width: 3.3, height: 6, rx: 1, fill: 'none', stroke: BOARDS, 'stroke-width': thin }, c);
        el('rect', { x: 189, y: 39.5, width: 3.3, height: 6, rx: 1, fill: 'none', stroke: BOARDS, 'stroke-width': thin }, c);

        el('rect', { x: 0, y: 0, width: LEN, height: WID, rx: 28, ry: 28, fill: 'none', stroke: BOARDS, 'stroke-width': 0.6 }, g);
    }

    function halo(attrs, w) {
        attrs.stroke = '#fff';
        attrs['stroke-width'] = w;
        attrs['stroke-linejoin'] = 'round';
        attrs['paint-order'] = 'stroke';
        return attrs;
    }

    function drawMarkGlyph(parent, kind, ink, angle, item) {
        var inner = el('g', { transform: MARKS[kind].rot && angle ? 'rotate(' + f2(angle) + ')' : null }, parent);
        MARKS[kind].draw(inner, ink, item);
    }

    // One item, in view coordinates. Hit areas are left out of an export.
    function drawItem(parent, item, view, k, index, forExport) {
        var ink = INKS[item.color] || INKS.black;
        var g = el('g', { 'data-i': forExport ? null : index }, parent);

        if (item.t === 'path') {
            var vp = item.pts.map(function (p) { return toView(view, p); });
            var poly = item.str ? vp : catmull(vp);
            pathShapes(item.style, item.end, poly, 1).forEach(function (sh) {
                el('path', {
                    d: sh.d, fill: sh.fill ? ink : 'none', stroke: sh.fill ? 'none' : ink,
                    'stroke-width': sh.fill ? null : f2(sh.w), 'stroke-linecap': 'round', 'stroke-linejoin': 'round',
                    'stroke-dasharray': sh.dash ? sh.dash.map(f2).join(' ') : null
                }, g);
            });
            // A wide invisible stroke, so a finger can pick up a thin line.
            if (!forExport)
                el('path', { d: pd(poly), fill: 'none', stroke: 'transparent', 'stroke-width': f2(3 * k), 'stroke-linecap': 'round' }, g);
            return g;
        }

        var v = toView(view, [item.x, item.y]);
        if (item.t === 'text') {
            var tg = el('g', { transform: 'translate(' + f2(v[0]) + ' ' + f2(v[1]) + ')' }, g);
            if (!forExport)
                el('rect', { x: f2(-0.7 * item.s.length), y: f2(-1.4 * k), width: f2(1.4 * item.s.length), height: f2(2.8 * k), fill: 'transparent' }, tg);
            var t = el('text', halo({
                x: 0, y: 0.85, 'font-size': 2.4, 'font-weight': 650, 'font-family': FONT,
                'text-anchor': 'middle', fill: ink
            }, 0.55), tg);
            t.textContent = item.s;
            return g;
        }

        // GLYPH_FT per glyph unit: a letter about 2.7 ft tall, an X about 2.6 ft across. Lines
        // match the coach's reference drill exactly; stamps sit a little larger than its bare
        // letters because the reference wraps each one in a 5 ft badge and these have none.
        var s = GLYPH_FT;
        var outer = el('g', { transform: 'translate(' + f2(v[0]) + ' ' + f2(v[1]) + ') scale(' + f2(s) + ')' }, g);
        // The hit area keeps a finger's size in every view, however small the stamp is drawn.
        if (!forExport) {
            var hit = Math.max(5.5, 2.6 * k / s);
            el('rect', { x: f2(-hit), y: f2(-hit), width: f2(2 * hit), height: f2(2 * hit), fill: 'transparent' }, outer);
        }

        if (item.t === 'num') {
            el('circle', { r: 3.5, fill: '#fff', stroke: ink, 'stroke-width': 0.9 }, outer);
            el('text', {
                x: 0, y: 1.85, 'font-size': 5.2, 'font-weight': 700, 'font-family': FONT,
                'text-anchor': 'middle', fill: ink
            }, outer).textContent = String(item.n);
            return g;
        }

        drawMarkGlyph(outer, item.kind, ink, item.rot + turnOf(view), item);
        if (item.label) {
            el('text', halo({
                x: 3.6, y: 5.4, 'font-size': 5, 'font-weight': 700, 'font-family': FONT, fill: ink
            }, 1.4), outer).textContent = item.label;
        }
        return g;
    }

    function drawScene(svg, model, view, forExport) {
        var k = scaleOf(view);
        drawRink(svg, view);
        var items = el('g', { 'class': 'hps-items' }, svg);
        model.items.forEach(function (it, i) { drawItem(items, it, view, k, i, forExport); });
        return items;
    }

    // ── Export ──────────────────────────────────────────────────────────
    // Rendered at the stored picture width on white, whatever the site theme, because the result
    // is a picture that also gets printed. The server re-encodes the PNG to lossless WebP.
    var EXPORT_WIDTH = 1600;

    // The finished picture as an SVG element, exactly what the PNG is made from.
    function toSvg(model) {
        var view = model.rink, r = viewRect(view), m = 1.2 * scaleOf(view);
        var vw = r.w + 2 * m, vh = r.h + 2 * m;
        var W = EXPORT_WIDTH, H = Math.round(W * vh / vw);
        var svg = el('svg', {
            width: W, height: H,
            viewBox: [r.x - m, r.y - m, vw, vh].map(f2).join(' ')
        });
        el('rect', { x: r.x - m, y: r.y - m, width: vw, height: vh, fill: '#fff' }, svg);
        drawScene(svg, model, view, true);
        return svg;
    }

    function exportPng(model) {
        var svg = toSvg(model);
        var W = +svg.getAttribute('width'), H = +svg.getAttribute('height');

        var text = new XMLSerializer().serializeToString(svg);
        var url = URL.createObjectURL(new Blob([text], { type: 'image/svg+xml' }));

        return new Promise(function (resolve, reject) {
            var img = new Image();
            img.onload = function () {
                try {
                    var canvas = document.createElement('canvas');
                    canvas.width = W; canvas.height = H;
                    var ctx = canvas.getContext('2d');
                    ctx.fillStyle = '#fff';
                    ctx.fillRect(0, 0, W, H);
                    ctx.drawImage(img, 0, 0, W, H);
                    URL.revokeObjectURL(url);
                    canvas.toBlob(function (blob) {
                        if (blob) resolve(blob); else reject(new Error('This browser could not make the picture.'));
                    }, 'image/png');
                } catch (e) {
                    URL.revokeObjectURL(url);
                    reject(new Error('This browser could not make the picture.'));
                }
            };
            img.onerror = function () {
                URL.revokeObjectURL(url);
                reject(new Error('This browser could not make the picture.'));
            };
            img.src = url;
        });
    }

    // ── Palette icons ───────────────────────────────────────────────────
    function pathIcon(style) {
        // Thicker than the real line, or a 42px icon of a hairline is indistinguishable from the
        // next one.
        var svg = el('svg', { viewBox: '0 0 48 16', width: 42, height: 14, 'aria-hidden': 'true', focusable: 'false' });
        pathShapes(style, PATHS[style].end, [[2, 8], [46, 8]], 2.1).forEach(function (sh) {
            el('path', {
                d: sh.d, fill: sh.fill ? 'currentColor' : 'none', stroke: sh.fill ? 'none' : 'currentColor',
                'stroke-width': sh.fill ? null : f2(sh.w * 2.2), 'stroke-linecap': 'round', 'stroke-linejoin': 'round',
                'stroke-dasharray': sh.dash ? sh.dash.map(f2).join(' ') : null
            }, svg);
        });
        return svg;
    }
    function markIcon(kind) {
        var svg = el('svg', { viewBox: '-6 -6 12 12', width: 22, height: 22, 'aria-hidden': 'true', focusable: 'false' });
        if (MARKS[kind].scaled) {
            // Drawn to scale it would not fit the icon.
            var len = 10;
            el('rect', { x: -len / 2, y: -1.1, width: len, height: 2.2, rx: 0.7, fill: 'currentColor', 'fill-opacity': 0.3,
                         stroke: 'currentColor', 'stroke-width': 0.7 }, svg);
        } else {
            drawMarkGlyph(svg, kind, 'currentColor', 0);
        }
        return svg;
    }
    function glyphIcon(draw) {
        var svg = el('svg', { viewBox: '-6 -6 12 12', width: 22, height: 22, 'aria-hidden': 'true', focusable: 'false' });
        draw(svg);
        return svg;
    }

    // ── Draft storage ───────────────────────────────────────────────────
    // Wrapped every time: storage can be missing or throw (private windows, blocked site data),
    // and the editor has to work without it.
    function readDraft(key) {
        if (!key) return null;
        try {
            var d = JSON.parse(localStorage.getItem(key) || 'null');
            return d && typeof d.json === 'string' ? d : null;
        } catch (e) { return null; }
    }
    function writeDraft(key, json, staged) {
        if (!key) return;
        try { localStorage.setItem(key, JSON.stringify({ json: json, staged: !!staged, at: Date.now() })); }
        catch (e) { /* drawing still works; it just can't be recovered */ }
    }
    function dropDraft(key) {
        if (!key) return;
        try { localStorage.removeItem(key); } catch (e) { }
    }

    // ── The editor ──────────────────────────────────────────────────────
    var openNow = false;

    /**
     * Opens the editor over the page.
     *   options.sketch     JSON of a drawing to edit, or null for a new one
     *   options.title      heading, e.g. "Edit drawing"
     *   options.starters   [{ label, sketch }] offered as starting points for a new drawing
     *   options.draft      { key, keepOnUse, offerStaged } autosave; see readDraft/writeDraft
     *   options.onUse      function ({ png: Blob, json: string }) -> value or Promise. Throwing or
     *                      rejecting with an Error keeps the editor open and shows its message.
     * Returns a Promise that resolves true when a drawing was used, false on cancel.
     */
    function open(options) {
        if (openNow) return Promise.resolve(false);
        openNow = true;
        options = options || {};

        var draft = options.draft || {};
        var initial = parse(options.sketch);
        var model = initial || { v: 1, rink: 'full', items: [] };
        var initialJson = serialize(model);
        var history = [initialJson], hIndex = 0;

        var st = {
            tool: 'select', end: 'arrow', color: 'black', mode: 'free', straight: false, text: ''
        };
        var selected = -1;
        var zoom = { s: 1, cx: 0, cy: 0 };
        var penSeen = false;
        var pointers = {};
        var gesture = null;     // the pointer interaction in progress
        var pending = null;     // a tapped-points line being built: { pts: [view points], hover }
        var spaceHeld = false;
        var lastFocus = document.activeElement;
        var finished;

        // ── DOM ──
        var root = h('div', { className: 'hps', role: 'dialog', 'aria-modal': 'true', 'aria-label': options.title || 'Draw a diagram', tabindex: '-1' });
        var top = h('div', { className: 'hps-top' }, root);
        var bCancel = h('button', { type: 'button', className: 'hp-btn hp-btn-quiet hp-btn-sm' }, top, 'Cancel');
        h('strong', { className: 'hps-title' }, top, options.title || 'Draw a diagram');
        var edits = h('div', { className: 'hps-group' }, top);
        var bUndo = h('button', { type: 'button', className: 'hp-btn hp-btn-quiet hp-btn-sm', title: 'Undo (Ctrl+Z)' }, edits, 'Undo');
        var bRedo = h('button', { type: 'button', className: 'hp-btn hp-btn-quiet hp-btn-sm', title: 'Redo (Ctrl+Y)' }, edits, 'Redo');
        var bDup = h('button', { type: 'button', className: 'hp-btn hp-btn-quiet hp-btn-sm', title: 'Duplicate (Ctrl+D)' }, edits, 'Duplicate');
        var bDel = h('button', { type: 'button', className: 'hp-btn hp-btn-quiet hp-btn-sm hps-del', title: 'Delete (Delete key)' }, edits, 'Delete');
        var viewSel = h('select', { className: 'hp-select hps-viewsel', 'aria-label': 'Rink view' }, top);
        VIEW_ORDER.forEach(function (v) { h('option', { value: v }, viewSel, VIEWS[v].label); });
        var bFinish = h('button', { type: 'button', className: 'hp-btn hp-btn-sm', hidden: true }, top, 'Finish line');
        var bFit = h('button', { type: 'button', className: 'hp-btn hp-btn-quiet hp-btn-sm', hidden: true }, top, 'Zoom out');
        var bUse = h('button', { type: 'button', className: 'hp-btn hp-btn-sm hps-use' }, top, 'Use this diagram');

        var body = h('div', { className: 'hps-body' }, root);
        var palette = h('div', { className: 'hps-palette' }, body);
        var stage = h('div', { className: 'hps-stage' }, body);
        // Messages sit in the rink's column, not across the editor: see .hps-msgs in the CSS.
        var msgs = h('div', { className: 'hps-msgs' }, stage);
        var svg = el('svg', { 'class': 'hps-svg', role: 'img', 'aria-label': 'Rink drawing' }, stage);
        var errBox = h('div', { className: 'hps-msg hps-msg-err', role: 'alert', hidden: true }, msgs);

        // ── Palette ──
        var toolButtons = {};
        function section(title) {
            var s = h('div', { className: 'hps-sec' }, palette);
            h('div', { className: 'hps-h' }, s, title);
            return h('div', { className: 'hps-tools' }, s);
        }
        function toolButton(parent, id, label, icon) {
            var b = h('button', { type: 'button', className: 'hps-tool', 'aria-pressed': 'false', title: label }, parent);
            b.appendChild(icon);
            h('span', {}, b, label);
            b.addEventListener('click', function () { setTool(id); });
            toolButtons[id] = b;
        }

        var collapse = h('button', { type: 'button', className: 'hps-collapse', 'aria-expanded': 'true' }, palette, 'Hide tools');
        collapse.addEventListener('click', function () {
            var hidden = root.classList.toggle('hps-collapsed');
            collapse.setAttribute('aria-expanded', hidden ? 'false' : 'true');
            collapse.textContent = hidden ? 'Show tools' : 'Hide tools';
        });

        var secTools = section('Pick');
        toolButton(secTools, 'select', 'Select and move', glyphIcon(function (s) {
            el('path', { d: 'M-3.5,-4.5L3.2,1L0.2,1.4L2,4.8L0.6,5.4L-1.1,2.1L-3.5,4Z', fill: 'currentColor' }, s);
        }));
        var secLines = section('Lines');
        PATH_ORDER.forEach(function (style) { toolButton(secLines, 'path:' + style, PATHS[style].label, pathIcon(style)); });
        var secMarks = section('Stamps');
        MARK_ORDER.forEach(function (kind) { toolButton(secMarks, 'mark:' + kind, MARKS[kind].label, markIcon(kind)); });
        toolButton(secMarks, 'num', 'Order number', glyphIcon(function (s) {
            el('circle', { r: 4.2, fill: 'none', stroke: 'currentColor', 'stroke-width': 0.9 }, s);
            el('text', { y: 2, 'font-size': 6, 'font-weight': 700, 'font-family': FONT, 'text-anchor': 'middle', fill: 'currentColor' }, s).textContent = '1';
        }));
        toolButton(secMarks, 'text', 'Text', glyphIcon(function (s) {
            el('text', { y: 3, 'font-size': 9, 'font-weight': 700, 'font-family': FONT, 'text-anchor': 'middle', fill: 'currentColor' }, s).textContent = 'Aa';
        }));

        h('p', { className: 'hps-hint' }, secMarks.parentNode,
            'To number a player (F1, D2), tap the stamp and type in the Label box beside it. ' +
            'To lengthen a blocker, drag the handle on its end.');

        var secOpts = h('div', { className: 'hps-sec hps-opts' }, palette);

        h('div', { className: 'hps-h' }, secOpts, 'Line end');
        var endRow = h('div', { className: 'hps-seg', role: 'group', 'aria-label': 'Line end' }, secOpts);
        var endButtons = {};
        Object.keys(ENDS).forEach(function (e) {
            var b = h('button', { type: 'button', 'aria-pressed': 'false' }, endRow, ENDS[e]);
            b.addEventListener('click', function () { setEnd(e); });
            endButtons[e] = b;
        });

        h('div', { className: 'hps-h' }, secOpts, 'Colour');
        var inkRow = h('div', { className: 'hps-inks', role: 'group', 'aria-label': 'Colour' }, secOpts);
        var inkButtons = {};
        INK_ORDER.forEach(function (name) {
            var b = h('button', { type: 'button', className: 'hps-ink', 'aria-pressed': 'false', title: name, 'aria-label': name }, inkRow);
            b.style.background = INKS[name];
            b.addEventListener('click', function () { setColor(name); });
            inkButtons[name] = b;
        });

        h('div', { className: 'hps-h' }, secOpts, 'Draw lines by');
        var modeRow = h('div', { className: 'hps-seg', role: 'group', 'aria-label': 'Draw lines by' }, secOpts);
        var modeButtons = {
            free: h('button', { type: 'button', 'aria-pressed': 'true' }, modeRow, 'Dragging'),
            points: h('button', { type: 'button', 'aria-pressed': 'false' }, modeRow, 'Tapping points')
        };
        Object.keys(modeButtons).forEach(function (m) {
            modeButtons[m].addEventListener('click', function () { finishPending(); st.mode = m; syncPalette(); });
        });
        var straightLabel = h('label', { className: 'hps-check' }, secOpts);
        var straightBox = h('input', { type: 'checkbox' }, straightLabel);
        h('span', {}, straightLabel, 'Straight lines (or hold Shift)');
        straightBox.addEventListener('change', function () { st.straight = straightBox.checked; });

        // The label box floats beside the selected stamp rather than living in the palette. In the
        // palette it sat below the colours and line options, off the bottom of the screen, and a
        // coach looking for how to make an F into F1 never found it.
        var labelPop = h('label', { className: 'hps-labelpop', hidden: true }, stage);
        h('span', {}, labelPop, 'Label');
        var labelInput = h('input', { type: 'text', maxlength: '2', autocomplete: 'off', autocapitalize: 'characters',
                                      'aria-label': 'Label for this stamp, up to 2 characters' }, labelPop);
        var lenNote = h('span', { className: 'hps-len', hidden: true }, labelPop);
        labelInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === 'Escape') { e.preventDefault(); labelInput.blur(); }
        });

        var textWrap = h('label', { className: 'hps-field' }, secOpts);
        h('span', { className: 'hps-h' }, textWrap, 'Text to place');
        var textInput = h('input', { type: 'text', className: 'hp-input', maxlength: '40', autocomplete: 'off', placeholder: 'e.g. Wait for pass' }, textWrap);

        labelInput.addEventListener('input', function () {
            var label = labelInput.value.trim().slice(0, 2);
            var it = model.items[selected];
            if (it && it.t === 'mark') {
                if (label) it.label = label; else delete it.label;
                render();
            }
        });
        labelInput.addEventListener('change', function () { if (model.items[selected]) commit(); });
        textInput.addEventListener('input', function () {
            st.text = textInput.value.slice(0, 40);
            var it = model.items[selected];
            if (it && it.t === 'text' && st.text.trim()) { it.s = st.text.trim(); render(); }
        });
        textInput.addEventListener('change', function () { if (model.items[selected]) commit(); });

        // ── Messages ──
        function banner(cls, text, actions) {
            var m = h('div', { className: 'hps-msg ' + (cls || '') }, msgs);
            h('span', {}, m, text);
            var row = h('span', { className: 'hps-msg-actions' }, m);
            (actions || []).forEach(function (a) {
                var b = h('button', { type: 'button', className: 'hp-btn hp-btn-quiet hp-btn-sm' }, row, a.label);
                b.addEventListener('click', function () { if (a.run) a.run(); m.remove(); });
            });
            return m;
        }
        function showError(text) {
            errBox.textContent = text;
            errBox.hidden = !text;
        }

        var restoreBanner = null, startersBanner = null;
        var saved = readDraft(draft.key);
        if (saved && saved.json !== initialJson && (!saved.staged || draft.offerStaged)) {
            restoreBanner = banner('', 'Restore your unsaved drawing?', [
                { label: 'Restore', run: function () {
                    var m = parse(saved.json);
                    if (m) { model = m; viewSel.value = model.rink; resetZoom(); selected = -1; commit(); }
                } },
                { label: 'Discard', run: function () { dropDraft(draft.key); } }
            ]);
        }
        if (!initial && options.starters && options.starters.length) {
            var acts = options.starters.slice(0, 6).map(function (s) {
                return { label: s.label, run: function () {
                    var m = parse(s.sketch);
                    if (m) { model = m; viewSel.value = model.rink; resetZoom(); selected = -1; commit(); }
                } };
            });
            acts.push({ label: 'Blank rink' });
            startersBanner = banner('', 'Start from another drawing on this drill?', acts);
        }
        if (window.innerWidth < 700) {
            banner('hps-msg-quiet', 'Easier on a tablet or computer. Pinch to zoom.',
                [{ label: 'OK' }]);
        }

        // ── In-page confirm, never window.confirm ──
        function ask(text, yes, no) {
            return new Promise(function (resolve) {
                var shade = h('div', { className: 'hps-ask' }, root);
                var box = h('div', { className: 'hps-ask-box', role: 'alertdialog', 'aria-modal': 'true' }, shade);
                h('p', {}, box, text);
                var row = h('div', { className: 'hps-ask-row' }, box);
                var bNo = h('button', { type: 'button', className: 'hp-btn hp-btn-quiet' }, row, no);
                var bYes = h('button', { type: 'button', className: 'hp-btn' }, row, yes);
                function done(v) { shade.remove(); resolve(v); }
                bNo.addEventListener('click', function () { done(false); });
                bYes.addEventListener('click', function () { done(true); });
                bNo.focus();
            });
        }

        // ── State changes ──
        function commit() {
            var json = serialize(model);
            if (json === history[hIndex]) { syncButtons(); return; }
            history = history.slice(0, hIndex + 1);
            history.push(json);
            if (history.length > 150) history.shift();
            hIndex = history.length - 1;
            writeDraft(draft.key, json, false);
            [restoreBanner, startersBanner].forEach(function (b) { if (b) b.remove(); });
            restoreBanner = startersBanner = null;
            render();
        }
        function restore(json) {
            model = cleanModel(JSON.parse(json));
            if (viewSel.value !== model.rink) { viewSel.value = model.rink; resetZoom(); }
            if (selected >= model.items.length) selected = -1;
            writeDraft(draft.key, json, false);
            render();
        }
        function undo() { cancelGesture(); finishPending(true); if (hIndex > 0) { hIndex--; restore(history[hIndex]); } }
        function redo() { cancelGesture(); if (hIndex < history.length - 1) { hIndex++; restore(history[hIndex]); } }
        function dirty() { return serialize(model) !== initialJson; }

        function select(i) {
            selected = i;
            var it = model.items[i];
            if (it) {
                if (it.color) st.color = it.color;
                if (it.t === 'path') st.end = it.end;
                if (it.t === 'mark') { labelInput.value = it.label || ''; }
                if (it.t === 'text') { textInput.value = it.s; st.text = it.s; }
            }
            render();
        }
        function setTool(id) {
            finishPending();
            st.tool = id;
            if (id.indexOf('path:') === 0) st.end = PATHS[id.slice(5)].end;
            if (id !== 'select') selected = -1;
            render();
        }
        function setEnd(e) {
            st.end = e;
            var it = model.items[selected];
            if (it && it.t === 'path') { it.end = e; commit(); } else render();
        }
        function setColor(c) {
            st.color = c;
            var it = model.items[selected];
            if (it) { it.color = c; commit(); } else render();
        }
        function deleteSelected() {
            if (!model.items[selected]) return;
            model.items.splice(selected, 1);
            selected = -1;
            commit();
        }
        function duplicateSelected() {
            var it = model.items[selected];
            if (!it || model.items.length >= MAX_ITEMS) return;
            var copy = JSON.parse(JSON.stringify(it));
            var k = scaleOf(model.rink);
            moveItem(copy, deltaToRink(model.rink, [3 * k, 3 * k]));
            if (copy.t === 'num') copy.n = nextNumber();
            model.items.push(copy);
            selected = model.items.length - 1;
            commit();
        }
        function moveItem(it, d) {
            if (it.t === 'path') it.pts = it.pts.map(function (p) { return [r1(p[0] + d[0]), r1(p[1] + d[1])]; });
            else { it.x = r1(it.x + d[0]); it.y = r1(it.y + d[1]); }
        }
        function nextNumber() {
            var n = 0;
            model.items.forEach(function (it) { if (it.t === 'num') n = Math.max(n, it.n); });
            return Math.min(99, n + 1);
        }

        function changeView(next) {
            var outside = model.items.filter(function (it) {
                var pts = it.t === 'path' ? it.pts : [[it.x, it.y]];
                return pts.some(function (p) { return !inside(next, p, 1); });
            }).length;
            function go() { model.rink = next; resetZoom(); commit(); render(); }
            if (!outside) { go(); return; }
            ask(outside + (outside === 1 ? ' thing is' : ' things are') + ' outside the ' + VIEWS[next].label.toLowerCase() +
                ' view and won\'t be in the picture. They stay in the drawing, so switching back brings them back.',
                'Switch anyway', 'Keep this view').then(function (ok) {
                if (ok) go(); else viewSel.value = model.rink;
            });
        }

        // ── Rendering ──
        var itemsLayer, overlay;
        function render() {
            while (svg.firstChild) svg.removeChild(svg.firstChild);
            itemsLayer = drawScene(svg, model, model.rink, false);
            overlay = el('g', { 'class': 'hps-overlay' }, svg);
            drawOverlay();
            applyViewBox();
            syncPalette();
            syncButtons();
        }
        // A blocker's long axis in view coordinates: its centre, unit direction, and the end that
        // carries the length handle.
        function blockerAxis(it) {
            var c = toView(model.rink, [it.x, it.y]);
            var a = (it.rot + turnOf(model.rink)) * Math.PI / 180, dir = [Math.cos(a), Math.sin(a)];
            return { c: c, dir: dir, end: [c[0] + dir[0] * it.len / 2, c[1] + dir[1] * it.len / 2] };
        }

        // Puts the label box just below and right of the selected stamp, kept inside the stage.
        function placeLabelPop(vx, vy) {
            var ctm = svg.getScreenCTM();
            if (!ctm) return;
            var p = svg.createSVGPoint();
            p.x = vx; p.y = vy;
            p = p.matrixTransform(ctm);
            var box = stage.getBoundingClientRect();
            var sel = model.items[selected];
            lenNote.hidden = !(sel && sel.kind === 'blocker');
            if (!lenNote.hidden) lenNote.textContent = sel.len + ' ft';
            labelPop.hidden = false;
            var w = labelPop.offsetWidth || 110, hgt = labelPop.offsetHeight || 40;
            labelPop.style.left = Math.max(4, Math.min(box.width - w - 4, p.x - box.left + 4)) + 'px';
            labelPop.style.top = Math.max(4, Math.min(box.height - hgt - 4, p.y - box.top + 4)) + 'px';
        }
        function drawOverlay() {
            while (overlay.firstChild) overlay.removeChild(overlay.firstChild);
            var sel = model.items[selected];
            if (!sel || sel.t !== 'mark') labelPop.hidden = true;
            var k = scaleOf(model.rink), z = 1 / zoom.s;
            var it = model.items[selected];
            if (it) {
                var g = itemsLayer.querySelector('[data-i="' + selected + '"]');
                if (g) {
                    var b = g.getBBox(), pad = 0.8 * k;
                    el('rect', {
                        x: b.x - pad, y: b.y - pad, width: b.width + 2 * pad, height: b.height + 2 * pad,
                        fill: 'none', stroke: '#1e88e5', 'stroke-width': f2(0.25 * k * z),
                        'stroke-dasharray': f2(0.8 * k * z) + ' ' + f2(0.5 * k * z), 'pointer-events': 'none'
                    }, overlay);
                    if (it.t === 'mark') placeLabelPop(b.x + b.width + pad, b.y + b.height + pad);
                    if (it.t === 'mark' && MARKS[it.kind].rot) {
                        var v = toView(model.rink, [it.x, it.y]), hy = b.y - pad - 3 * k * z;
                        el('line', { x1: v[0], y1: b.y - pad, x2: v[0], y2: hy, stroke: '#1e88e5', 'stroke-width': f2(0.25 * k * z), 'pointer-events': 'none' }, overlay);
                        el('circle', { cx: v[0], cy: hy, r: f2(1.3 * k * z), fill: '#fff', stroke: '#1e88e5', 'stroke-width': f2(0.35 * k * z), 'data-h': 'rot', 'class': 'hps-rot' }, overlay);
                    }
                    if (it.t === 'mark' && it.kind === 'blocker') {
                        // A square, so it reads as a different handle from the round rotate one.
                        var ax = blockerAxis(it), hs = 1.1 * k * z;
                        el('rect', { x: f2(ax.end[0] - hs), y: f2(ax.end[1] - hs), width: f2(2 * hs), height: f2(2 * hs),
                                     fill: '#fff', stroke: '#1e88e5', 'stroke-width': f2(0.35 * k * z), 'data-h': 'len', 'class': 'hps-len-h' }, overlay);
                    }
                }
            }
            if (gesture && gesture.kind === 'stroke' && gesture.pts.length > 1) {
                var pts = gesture.straight ? [gesture.pts[0], gesture.pts[gesture.pts.length - 1]] : gesture.pts;
                el('path', { d: pd(pts), fill: 'none', stroke: INKS[st.color], 'stroke-width': 0.3, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', opacity: 0.7, 'pointer-events': 'none' }, overlay);
            }
            if (pending && pending.pts.length) {
                var all = pending.pts.concat(pending.hover ? [pending.hover] : []);
                if (all.length > 1) {
                    var poly = st.straight ? all : catmull(all);
                    pathShapes(st.tool.slice(5), st.end, poly, 1).forEach(function (sh) {
                        el('path', {
                            d: sh.d, fill: sh.fill ? INKS[st.color] : 'none', stroke: sh.fill ? 'none' : INKS[st.color],
                            'stroke-width': sh.fill ? null : f2(sh.w), 'stroke-linecap': 'round', opacity: 0.75,
                            'stroke-dasharray': sh.dash ? sh.dash.map(f2).join(' ') : null, 'pointer-events': 'none'
                        }, overlay);
                    });
                }
                pending.pts.forEach(function (p) {
                    el('circle', { cx: p[0], cy: p[1], r: f2(0.6 * k * z), fill: '#1e88e5', 'pointer-events': 'none' }, overlay);
                });
            }
        }
        function syncPalette() {
            Object.keys(toolButtons).forEach(function (id) {
                toolButtons[id].setAttribute('aria-pressed', id === st.tool ? 'true' : 'false');
            });
            var it = model.items[selected];
            var pathish = st.tool.indexOf('path:') === 0 || (it && it.t === 'path');
            Object.keys(endButtons).forEach(function (e) {
                endButtons[e].setAttribute('aria-pressed', e === st.end ? 'true' : 'false');
                endButtons[e].disabled = !pathish;
            });
            INK_ORDER.forEach(function (n) { inkButtons[n].setAttribute('aria-pressed', n === st.color ? 'true' : 'false'); });
            Object.keys(modeButtons).forEach(function (m) { modeButtons[m].setAttribute('aria-pressed', m === st.mode ? 'true' : 'false'); });
            textWrap.hidden = !(st.tool === 'text' || (it && it.t === 'text'));
            stage.setAttribute('data-tool', st.tool.split(':')[0]);
        }
        function syncButtons() {
            bUndo.disabled = hIndex === 0;
            bRedo.disabled = hIndex >= history.length - 1;
            bDel.disabled = bDup.disabled = !model.items[selected];
            bFinish.hidden = !pending;
            bFit.hidden = zoom.s <= 1.01;
        }

        // ── Zoom and pan, on the viewBox ──
        function baseBox() {
            var r = viewRect(model.rink), m = 2 * scaleOf(model.rink);
            return { x: r.x - m, y: r.y - m, w: r.w + 2 * m, h: r.h + 2 * m };
        }
        function resetZoom() {
            var b = baseBox();
            zoom = { s: 1, cx: b.x + b.w / 2, cy: b.y + b.h / 2 };
        }
        function applyViewBox() {
            var b = baseBox(), w = b.w / zoom.s, hh = b.h / zoom.s;
            zoom.cx = Math.min(b.x + b.w - w / 2, Math.max(b.x + w / 2, zoom.cx));
            zoom.cy = Math.min(b.y + b.h - hh / 2, Math.max(b.y + hh / 2, zoom.cy));
            svg.setAttribute('viewBox', [zoom.cx - w / 2, zoom.cy - hh / 2, w, hh].map(f2).join(' '));
        }
        function clientToView(x, y) {
            var ctm = svg.getScreenCTM();
            if (!ctm) return [0, 0];
            var p = svg.createSVGPoint();
            p.x = x; p.y = y;
            p = p.matrixTransform(ctm.inverse());
            return [p.x, p.y];
        }
        function zoomAt(clientX, clientY, s) {
            var anchor = clientToView(clientX, clientY);
            zoom.s = Math.min(8, Math.max(1, s));
            applyViewBox();
            var now = clientToView(clientX, clientY);
            zoom.cx += anchor[0] - now[0];
            zoom.cy += anchor[1] - now[1];
            applyViewBox();
            drawOverlay();
            syncButtons();
        }

        // ── Pointer input ──
        function pointerList() { return Object.keys(pointers).map(function (id) { return pointers[id]; }); }

        function cancelGesture() {
            if (!gesture) return;
            if (gesture.kind === 'move' || gesture.kind === 'rotate' || gesture.kind === 'resize') restoreNoHistory();
            gesture = null;
            render();
        }
        function restoreNoHistory() {
            model = cleanModel(JSON.parse(history[hIndex]));
        }

        function hitIndex(target) {
            var g = target && target.closest ? target.closest('[data-i]') : null;
            return g ? parseInt(g.getAttribute('data-i'), 10) : -1;
        }

        function onDown(e) {
            if (e.pointerType === 'pen') penSeen = true;
            pointers[e.pointerId] = { id: e.pointerId, x: e.clientX, y: e.clientY, type: e.pointerType };
            try { svg.setPointerCapture(e.pointerId); } catch (err) { }
            e.preventDefault();
            showError('');
            // preventDefault above keeps focus where it was, so a label or text box being typed in
            // would never see its change event. Blur it, so the edit is committed as one step.
            if (typingIn(document.activeElement)) document.activeElement.blur();

            var list = pointerList();
            if (list.length === 2 && e.pointerType === 'touch') {
                cancelGesture();
                startPinch(list);
                return;
            }
            if (list.length > 2) return;

            var p = clientToView(e.clientX, e.clientY);
            var panOnly = (e.pointerType === 'touch' && penSeen) || e.button === 1 || (e.button === 0 && spaceHeld);
            if (panOnly) { gesture = { kind: 'pan', id: e.pointerId, anchor: p }; return; }
            if (e.button > 0) return;

            if (e.target.getAttribute && e.target.getAttribute('data-h') === 'rot' && model.items[selected]) {
                gesture = { kind: 'rotate', id: e.pointerId };
                return;
            }
            if (e.target.getAttribute && e.target.getAttribute('data-h') === 'len' && model.items[selected]) {
                // The far end stays where it is and only the dragged end moves, which is how you
                // would slide a real blocker's end along the boards.
                var ax0 = blockerAxis(model.items[selected]), len0 = model.items[selected].len;
                gesture = { kind: 'resize', id: e.pointerId, dir: ax0.dir,
                            fixed: [ax0.c[0] - ax0.dir[0] * len0 / 2, ax0.c[1] - ax0.dir[1] * len0 / 2] };
                return;
            }

            var hit = hitIndex(e.target);
            var tool = st.tool;

            if (tool === 'select') {
                if (hit >= 0) { select(hit); gesture = { kind: 'move', id: e.pointerId, from: p, orig: JSON.stringify(model.items[hit]), moved: false }; }
                else { selected = -1; render(); gesture = { kind: 'pan', id: e.pointerId, anchor: p }; }
                return;
            }

            if (tool.indexOf('mark:') === 0 || tool === 'num' || tool === 'text') {
                if (hit >= 0 && model.items[hit].t !== 'path') {
                    select(hit);
                    gesture = { kind: 'move', id: e.pointerId, from: p, orig: JSON.stringify(model.items[hit]), moved: false };
                    return;
                }
                // Placed on release, not here. On a phone the first finger of a pinch lands before
                // the second, and placing on contact left a stray stamp behind every zoom.
                gesture = { kind: 'place', id: e.pointerId, start: p };
                return;
            }

            if (tool.indexOf('path:') === 0) {
                if (st.mode === 'points') {
                    gesture = { kind: 'tap', id: e.pointerId, start: p };
                } else {
                    gesture = { kind: 'stroke', id: e.pointerId, pts: [p], straight: st.straight || e.shiftKey };
                }
            }
        }

        function placeStamp(p) {
            var tool = st.tool;
            if (model.items.length >= MAX_ITEMS) { showError('That is as many things as one drawing can hold.'); return; }
            var r = toRink(model.rink, p), item;
            if (tool === 'text') {
                var s = textInput.value.trim();
                if (!s) { showError('Type the text to place first, in the box under the tools.'); textInput.focus(); return; }
                item = { t: 'text', x: r1(r[0]), y: r1(r[1]), s: s.slice(0, 40), color: st.color };
            } else if (tool === 'num') {
                item = { t: 'num', x: r1(r[0]), y: r1(r[1]), n: nextNumber(), color: st.color };
            } else {
                // Placed upright in the view it was drawn in.
                item = { t: 'mark', kind: tool.slice(5), x: r1(r[0]), y: r1(r[1]), rot: -turnOf(model.rink), color: st.color };
                if (item.kind === 'blocker') item.len = BLOCKER_DEFAULT;
            }
            model.items.push(item);
            // Selected once placed, so the label box beside it labels this stamp.
            select(model.items.length - 1);
            commit();
        }

        function onMove(e) {
            var ptr = pointers[e.pointerId];
            if (ptr) { ptr.x = e.clientX; ptr.y = e.clientY; }

            if (pending && !gesture && st.tool.indexOf('path:') === 0 && e.pointerType !== 'touch') {
                pending.hover = clientToView(e.clientX, e.clientY);
                drawOverlay();
            }
            if (!gesture) return;
            if (gesture.kind === 'pinch') { movePinch(); return; }
            if (gesture.id !== e.pointerId) return;

            var p = clientToView(e.clientX, e.clientY);
            var k = scaleOf(model.rink);
            switch (gesture.kind) {
                case 'pan':
                    zoom.cx += gesture.anchor[0] - p[0];
                    zoom.cy += gesture.anchor[1] - p[1];
                    applyViewBox();
                    break;
                case 'stroke': {
                    var evs = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
                    if (!evs.length) evs = [e];
                    evs.forEach(function (ev) {
                        var q = clientToView(ev.clientX, ev.clientY), last = gesture.pts[gesture.pts.length - 1];
                        if (dist(q, last) > 0.15 * k) gesture.pts.push(q);
                    });
                    gesture.straight = st.straight || e.shiftKey;
                    drawOverlay();
                    break;
                }
                case 'move': {
                    var d = [p[0] - gesture.from[0], p[1] - gesture.from[1]];
                    if (!gesture.moved && Math.abs(d[0]) + Math.abs(d[1]) < 0.4 * k) return;
                    gesture.moved = true;
                    var it = JSON.parse(gesture.orig);
                    moveItem(it, deltaToRink(model.rink, d));
                    model.items[selected] = it;
                    render();
                    break;
                }
                case 'resize': {
                    var bl = model.items[selected];
                    var along = (p[0] - gesture.fixed[0]) * gesture.dir[0] + (p[1] - gesture.fixed[1]) * gesture.dir[1];
                    bl.len = Math.max(BLOCKER_MIN, Math.min(BLOCKER_MAX, Math.round(along)));
                    var mid = toRink(model.rink, [gesture.fixed[0] + gesture.dir[0] * bl.len / 2,
                                                  gesture.fixed[1] + gesture.dir[1] * bl.len / 2]);
                    bl.x = r1(mid[0]); bl.y = r1(mid[1]);
                    render();
                    break;
                }
                case 'rotate': {
                    var item = model.items[selected];
                    var c = toView(model.rink, [item.x, item.y]);
                    var deg = Math.atan2(p[1] - c[1], p[0] - c[0]) * 180 / Math.PI + 90;
                    if (!e.altKey) deg = Math.round(deg / 15) * 15;
                    item.rot = ((deg - turnOf(model.rink)) % 360 + 360) % 360;
                    render();
                    break;
                }
            }
        }

        function onUp(e) {
            delete pointers[e.pointerId];
            if (!gesture) return;
            if (gesture.kind === 'pinch') {
                if (pointerList().length < 2) gesture = null;
                return;
            }
            if (gesture.id !== e.pointerId) return;
            var g = gesture;
            gesture = null;
            var k = scaleOf(model.rink);

            if (g.kind === 'move' || g.kind === 'rotate' || g.kind === 'resize') {
                if (g.kind !== 'move' || g.moved) commit(); else render();
                return;
            }
            if (g.kind === 'stroke') { finishStroke(g, e.type === 'pointercancel'); return; }
            if (g.kind === 'place') {
                if (e.type !== 'pointercancel' && dist(clientToView(e.clientX, e.clientY), g.start) < 1.5 * k) placeStamp(g.start);
                return;
            }
            if (g.kind === 'tap') {
                if (e.type === 'pointercancel') return;
                var p = clientToView(e.clientX, e.clientY);
                if (dist(p, g.start) > 1.5 * k) { drawOverlay(); return; }
                if (!pending) pending = { pts: [] };
                var last = pending.pts[pending.pts.length - 1];
                // Tapping the last point again finishes the line, which is the natural gesture on
                // a phone where there is no double-click or Enter.
                if (last && dist(last, p) < 1.5 * k) { finishPending(); return; }
                pending.pts.push(p);
                pending.hover = null;
                drawOverlay();
                syncButtons();
            }
            if (g.kind === 'pan') drawOverlay();
        }

        function finishStroke(g, cancelled) {
            var k = scaleOf(model.rink);
            if (cancelled || g.pts.length < 2) { drawOverlay(); return; }
            var pts = g.straight ? [g.pts[0], g.pts[g.pts.length - 1]] : simplify(g.pts, 0.3 * k);
            if (measure(pts).L < 1.2 * k) { drawOverlay(); return; }
            addPath(pts, g.straight);
        }
        function addPath(viewPts, straight) {
            if (model.items.length >= MAX_ITEMS) { showError('That is as many things as one drawing can hold.'); return; }
            var style = st.tool.slice(5);
            var item = {
                t: 'path', style: style, end: st.end, color: st.color,
                pts: viewPts.slice(0, MAX_POINTS).map(function (v) { var r = toRink(model.rink, v); return [r1(r[0]), r1(r[1])]; })
            };
            if (straight) item.str = 1;
            model.items.push(item);
            commit();
        }
        function finishPending(discard) {
            if (!pending) return;
            var p = pending;
            pending = null;
            if (!discard && p.pts.length >= 2) addPath(p.pts, st.straight);
            else render();
        }

        function startPinch(list) {
            var a = list[0], b = list[1];
            var mid = [(a.x + b.x) / 2, (a.y + b.y) / 2];
            gesture = {
                kind: 'pinch', d0: Math.max(10, Math.hypot(a.x - b.x, a.y - b.y)), s0: zoom.s,
                anchor: clientToView(mid[0], mid[1])
            };
        }
        function movePinch() {
            var list = pointerList();
            if (list.length < 2) return;
            var a = list[0], b = list[1];
            var mid = [(a.x + b.x) / 2, (a.y + b.y) / 2];
            zoom.s = Math.min(8, Math.max(1, gesture.s0 * Math.hypot(a.x - b.x, a.y - b.y) / gesture.d0));
            applyViewBox();
            var now = clientToView(mid[0], mid[1]);
            zoom.cx += gesture.anchor[0] - now[0];
            zoom.cy += gesture.anchor[1] - now[1];
            applyViewBox();
            drawOverlay();
            syncButtons();
        }

        svg.addEventListener('pointerdown', onDown);
        svg.addEventListener('pointermove', onMove);
        svg.addEventListener('pointerup', onUp);
        svg.addEventListener('pointercancel', onUp);
        svg.addEventListener('dblclick', function (e) { if (pending) { e.preventDefault(); finishPending(); } });
        svg.addEventListener('wheel', function (e) {
            e.preventDefault();
            if (e.ctrlKey || e.metaKey) {
                zoomAt(e.clientX, e.clientY, zoom.s * Math.exp(-e.deltaY * 0.01));
            } else if (zoom.s > 1) {
                var a = clientToView(0, 0), b = clientToView(e.deltaX, e.deltaY);
                zoom.cx += b[0] - a[0];
                zoom.cy += b[1] - a[1];
                applyViewBox();
                drawOverlay();
            }
        }, { passive: false });

        // ── Keyboard ──
        function typingIn(t) { return t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA'); }
        function onKey(e) {
            if (root.querySelector('.hps-ask')) return;
            if (e.key === ' ' && !typingIn(e.target)) { spaceHeld = e.type === 'keydown'; if (spaceHeld) e.preventDefault(); return; }
            if (e.type !== 'keydown' || typingIn(e.target)) return;
            var mod = e.ctrlKey || e.metaKey;
            if (mod && e.key.toLowerCase() === 'z') { e.preventDefault(); if (e.shiftKey) redo(); else undo(); }
            else if (mod && e.key.toLowerCase() === 'y') { e.preventDefault(); redo(); }
            else if (mod && e.key.toLowerCase() === 'd') { e.preventDefault(); duplicateSelected(); }
            else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); deleteSelected(); }
            else if (e.key === 'Enter' && pending) { e.preventDefault(); finishPending(); }
            else if (e.key === 'Escape') {
                e.preventDefault();
                if (pending) finishPending(true);
                else if (selected >= 0) { selected = -1; render(); }
            }
        }
        document.addEventListener('keydown', onKey);
        document.addEventListener('keyup', onKey);

        // ── Buttons ──
        bUndo.addEventListener('click', undo);
        bRedo.addEventListener('click', redo);
        bDel.addEventListener('click', deleteSelected);
        bDup.addEventListener('click', duplicateSelected);
        bFinish.addEventListener('click', function () { finishPending(); });
        bFit.addEventListener('click', function () { resetZoom(); render(); });
        viewSel.addEventListener('change', function () { finishPending(); changeView(viewSel.value); });

        var busy = false;
        bUse.addEventListener('click', function () {
            if (busy) return;
            finishPending();
            if (!model.items.length) { showError('Draw something first.'); return; }
            busy = true;
            bUse.disabled = bCancel.disabled = true;
            bUse.textContent = 'Saving…';
            showError('');
            var json = serialize(model);
            exportPng(model).then(function (png) {
                return options.onUse ? options.onUse({ png: png, json: json }) : null;
            }).then(function () {
                if (draft.keepOnUse) writeDraft(draft.key, json, true); else dropDraft(draft.key);
                close(true);
            }, function (err) {
                busy = false;
                bUse.disabled = bCancel.disabled = false;
                bUse.textContent = 'Use this diagram';
                showError(err && err.message ? err.message : 'That didn\'t work. Your drawing is still here; try again.');
            });
        });

        bCancel.addEventListener('click', function () {
            finishPending(true);
            if (!dirty()) { close(false); return; }
            ask('Throw away this drawing?', 'Throw it away', 'Keep drawing').then(function (ok) {
                if (ok) { dropDraft(draft.key); close(false); }
            });
        });

        function close(used) {
            document.removeEventListener('keydown', onKey);
            document.removeEventListener('keyup', onKey);
            if (resizer) resizer.disconnect();
            root.remove();
            document.documentElement.classList.remove('hps-open');
            openNow = false;
            if (lastFocus && lastFocus.focus) { try { lastFocus.focus(); } catch (e) { } }
            finished(used);
        }

        // ── Go ──
        document.body.appendChild(root);
        document.documentElement.classList.add('hps-open');
        viewSel.value = model.rink;
        resetZoom();
        render();
        root.focus();

        // A finished drawing should never need a re-layout to be the right size.
        var resizer = window.ResizeObserver ? new ResizeObserver(function () { drawOverlay(); }) : null;
        if (resizer) resizer.observe(stage);

        return new Promise(function (resolve) { finished = resolve; });
    }

    window.HPSketch = {
        open: open,
        // Exposed for tests and for anything that wants to read a drawing without the editor.
        parse: parse,
        toSvg: toSvg,
        geometry: { simplify: simplify, catmull: catmull, pathShapes: pathShapes, toView: toView, toRink: toRink }
    };
})();
