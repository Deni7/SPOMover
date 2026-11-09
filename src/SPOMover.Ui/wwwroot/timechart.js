// Time charts of the run dashboard on uPlot (wwwroot/lib/uplot, MIT).
// Charts of one group share the time range: zoom by selecting with the mouse, double click shows the whole run.

const groups = new Map();
const pad = n => String(n).padStart(2, '0');
const hm = d => `${pad(d.getHours())}:${pad(d.getMinutes())}`;
const dm = d => `${pad(d.getDate())}.${pad(d.getMonth() + 1)}`;

function timeTicks(u, splits) {
    // Seconds only when the ticks are closer than a minute (a short run, or a deep zoom).
    const time = splits.length > 1 && splits[1] - splits[0] < 60 ? d => `${hm(d)}:${pad(d.getSeconds())}` : hm;
    let last = '';
    return splits.map(s => {
        const d = new Date(s * 1000);
        const day = dm(d);
        const text = day !== last ? `${day}\n${time(d)}` : time(d);
        last = day;
        return text;
    });
}

// The number format and the label of the time column come from the page, in the language of the UI.
const numbers = locale => new Intl.NumberFormat(locale || undefined, { maximumFractionDigits: 1 });
// Axis labels from a million on: "1,2 млн" instead of "1 200 000" (the legend keeps the full number).
const compact = locale => new Intl.NumberFormat(locale || undefined, { notation: 'compact', maximumFractionDigits: 1 });

const measure = document.createElement('canvas').getContext('2d');

// The width of a number axis follows its longest label, so no label is cut off at the edge.
function axisSize(font) {
    return (u, values) => {
        if (!values || values.length === 0)
            return 56;
        measure.font = font;
        const widest = Math.max(...values.map(v => measure.measureText(v ?? '').width));
        return Math.max(40, Math.ceil(widest) + 16);
    };
}

function options(el, spec) {
    const axis = { stroke: '#616161', grid: { stroke: '#eeeeee' }, ticks: { stroke: '#e0e0e0' }, font: '11px Segoe UI, Roboto, sans-serif' };
    const nf = numbers(spec.locale);
    const short = compact(spec.locale);
    const numberTicks = (u, splits) => {
        const big = splits.some(v => v != null && Math.abs(v) >= 1e6);
        return splits.map(v => v == null ? '' : (big ? short : nf).format(v));
    };
    const axes = [{ ...axis, values: timeTicks, space: 60, size: 40 }];
    const scales = { x: { time: true } };
    for (const [i, name] of spec.axes.entries()) {
        scales[name] = { auto: true, range: (u, min, max) => [0, max > 0 ? max * 1.05 : 1] };
        axes.push({ ...axis, scale: name, side: i === 0 ? 3 : 1, values: numberTicks, size: axisSize(axis.font), grid: { ...axis.grid, show: i === 0 } });
    }
    return {
        width: Math.max(el.clientWidth, 200),
        height: spec.height,
        scales,
        axes,
        series: [
            { label: spec.timeLabel || 'Time', value: (u, v) => v == null ? '—' : `${dm(new Date(v * 1000))} ${hm(new Date(v * 1000))}` },
            ...spec.series.map(s => ({
                label: s.name, stroke: s.color, width: 2, scale: s.axis, points: { show: false },
                value: (u, v) => v == null ? '—' : nf.format(v),
            })),
        ],
        cursor: { drag: { x: true, y: false }, sync: { key: spec.group } },
        legend: { live: true },
        hooks: { setSelect: [zoomed], setScale: [(u, key) => key === 'x' && follow(u)] },
    };
}

// A zoom by the mouse fixes the range for the whole group until a period is chosen again.
function zoomed(u) {
    if (u.select.width <= 0)
        return;
    const min = u.posToVal(u.select.left, 'x'), max = u.posToVal(u.select.left + u.select.width, 'x');
    groups.get(u._chart.group).mode = { kind: 'fixed', min, max };
}

// uPlot applies a scale after the call returns, so the other charts follow from this hook; equal ranges stop the echo.
function follow(u) {
    const chart = u._chart;
    if (!chart)
        return;
    const { min, max } = u.scales.x;
    for (const other of groups.get(chart.group).charts)
        if (other !== chart && (other.plot.scales.x.min !== min || other.plot.scales.x.max !== max))
            other.plot.setScale('x', { min, max });
}

function apply(chart) {
    const group = groups.get(chart.group);
    const xs = chart.plot.data[0];
    if (!xs || xs.length < 2)
        return;
    const first = xs[0], last = xs[xs.length - 1];
    let min = first, max = last;
    if (group.mode.kind === 'last')
        min = Math.max(first, last - group.mode.seconds);
    else if (group.mode.kind === 'fixed')
        ({ min, max } = group.mode);
    chart.plot.setScale('x', { min, max });
}

export function create(el, spec, data) {
    if (!window.uPlot)
        return null;
    if (!groups.has(spec.group))
        groups.set(spec.group, { charts: new Set(), mode: { kind: 'all' } });
    const chart = { group: spec.group };
    chart.plot = new window.uPlot(options(el, spec), data, el);
    chart.plot._chart = chart;
    // Double click shows the whole run (uPlot's own reset would leave the other charts zoomed).
    chart.plot.over.addEventListener('dblclick', e => { e.stopImmediatePropagation(); range(spec.group, 0); }, true);
    chart.resize = new ResizeObserver(() => chart.plot.setSize({ width: Math.max(el.clientWidth, 200), height: spec.height }));
    chart.resize.observe(el);
    groups.get(spec.group).charts.add(chart);
    apply(chart);
    return chart;
}

export function update(chart, data) {
    if (!chart)
        return;
    chart.plot.setData(data, false);
    apply(chart);
}

/** Shows the last <seconds> of the run in every chart of the group; 0 — the whole run. */
export function range(group, seconds) {
    const g = groups.get(group);
    if (!g)
        return;
    g.mode = seconds > 0 ? { kind: 'last', seconds } : { kind: 'all' };
    for (const chart of g.charts)
        apply(chart);
}

export function dispose(chart) {
    if (!chart)
        return;
    chart.resize.disconnect();
    chart.plot.destroy();
    const g = groups.get(chart.group);
    g?.charts.delete(chart);
    if (g && g.charts.size === 0)
        groups.delete(chart.group);
}
