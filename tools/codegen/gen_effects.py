# One-shot generator for src/UnifiedRgb.Core/Effects/Generators/*Effect.cs (kept for reproducibility).
# Re-run after editing an effect body here; the generated files are committed.
import os, textwrap, argparse
parser = argparse.ArgumentParser(description="Regenerate the 34 generic effect implementations, or verify them without writes.")
parser.add_argument('--check', action='store_true', help='Verify committed source; never rewrite files.')
parser.add_argument('--output', help='Optional explicit output directory (default: the repository Generators directory).')
args = parser.parse_args()
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'UnifiedRgb.Core', 'Effects', 'Generators')
if args.output:
    OUT = os.path.abspath(args.output)
if not args.check:
    os.makedirs(OUT, exist_ok=True)

E = {}
def e(name, display, local, mn, mx, speed, dirn, defaults, w19, doc, body):
    E[name] = (display, local, mn, mx, speed, dirn, defaults, w19, doc, body)

e('StaticColor','Static','单色',1,1,False,False,'One',True,'All LEDs show palette color 0.',"""
output.Fill(ctx.Color(0));""")

e('Rainbow','Rainbow','彩虹',0,0,True,True,'None',True,'Full spectrum spread along u, scrolling with time.',"""
var p = Frac(ctx.Phase(0.05, 1.0));
for (var i = 0; i < output.Length; i++)
{
    output[i] = ColorTables.RainbowAt(ctx.U(i) - p);
}""")

e('RainbowMorph','Rainbow Morph','幻彩',0,0,True,False,'None',True,'Whole zone cycles through the spectrum in unison.',"""
output.Fill(ColorTables.RainbowAt(Frac(ctx.Phase(0.02, 0.5))));""")

e('Breathing','Breathing','呼吸',1,7,True,False,'Trio',True,'Raised-cosine fade in/out; each breath advances to the next palette color.',"""
var ph = ctx.Phase(0.1, 1.0);
var f = Frac(ph);
var env = 0.5f - (0.5f * MathF.Cos(MathF.Tau * f));
output.Fill(Scale(ctx.Color(Floor(ph)), env * env));""")

e('Runway','Runway','跑道',1,7,True,True,'Trio',True,'A lit segment with a linear tail laps around the zone; the color changes every lap.',"""
var ph = ctx.Phase(0.1, 1.5);
var head = Frac(ph);
var c = ctx.Color(Floor(ph));
const float len = 0.3f;
for (var i = 0; i < output.Length; i++)
{
    var d = Frac(head - ctx.U(i));
    output[i] = d < len ? Scale(c, 1f - (d / len)) : Rgb.Black;
}""")

e('Meteor','Meteor','流星',1,7,True,True,'Trio',True,'A bright head with a decaying tail crosses the zone; color changes per pass.',"""
var ph = ctx.Phase(0.2, 2.0);
const float tail = 0.4f;
var head = Frac(ph) * (1f + tail);
var c = ctx.Color(Floor(ph));
for (var i = 0; i < output.Length; i++)
{
    output[i] = Scale(c, Tail(head - ctx.U(i), tail, 2.5f));
}""")

e('ColorCycle','Color Cycle','颜色轮询',2,7,True,False,'Trio',True,'Whole zone holds each palette color, then cross-fades to the next.',"""
var ph = ctx.Phase(0.1, 1.0);
var k = Floor(ph);
var m = SmoothStep(0.7f, 1f, Frac(ph));
output.Fill(Lerp(ctx.Color(k), ctx.Color(k + 1), m));""")

e('CoverCycle','Cover Cycle','覆盖轮询',2,7,True,True,'Trio',True,'The next palette color wipes over the current one along u.',"""
var ph = ctx.Phase(0.1, 1.0);
var k = Floor(ph);
var f = Frac(ph);
Rgb cur = ctx.Color(k), next = ctx.Color(k + 1);
for (var i = 0; i < output.Length; i++)
{
    output[i] = ctx.U(i) <= f ? next : cur;
}""")

e('Wave','Wave','波浪',1,2,True,True,'Duo',True,'Sinusoidal bands (two per zone) travel along u, blending color 0 over color 1.',"""
var ph = Frac(ctx.Phase(0.1, 1.0));
Rgb c0 = ctx.Color(0), c1 = Secondary(ctx);
for (var i = 0; i < output.Length; i++)
{
    var v = 0.5f + (0.5f * MathF.Sin(MathF.Tau * ((ctx.U(i) * 2f) - ph)));
    output[i] = Lerp(c1, c0, v);
}""")

e('MeteorShower','Meteor Shower','流星雨',0,7,True,True,'None',True,'Four meteors with hashed speeds/phases rain along u (additive).',"""
output.Clear();
var baseRate = ctx.Rate(0.2, 2.0);
const float tail = 0.2f;
for (var m = 0; m < 4; m++)
{
    var ph = (ctx.T * baseRate * (0.6 + (0.8 * Rand(ctx.Seed, m, 0)))) + Rand(ctx.Seed, m, 1);
    var head = Frac(ph) * (1f + tail);
    var c = ctx.Color(m + Floor(ph));
    for (var i = 0; i < output.Length; i++)
    {
        var v = Tail(head - ctx.U(i), tail, 2f);
        if (v > 0f)
        {
            output[i] = AddSat(output[i], Scale(c, v));
        }
    }
}""")

e('Twinkle','Twinkle','闪烁',0,7,True,False,'None',True,'Each LED independently sparkles at hashed times with hashed palette colors.',"""
var rate = ctx.Rate(0.5, 4.0);
for (var i = 0; i < output.Length; i++)
{
    var ph = (ctx.T * rate * (0.5 + Rand(ctx.Seed, i, 7))) + Rand(ctx.Seed, i, 8);
    var ep = Floor(ph);
    var on = Rand(ctx.Seed, (i * 7919L) + ep, 9) < 0.35f;
    output[i] = on ? Scale(ctx.Color(RandInt(ctx.Seed, ep, i, 7)), MathF.Sin(MathF.PI * Frac(ph))) : Rgb.Black;
}""")

e('Taichi','Taichi','太极',2,2,True,True,'Duo',False,'Two colors share the zone half/half with soft edges and rotate.',"""
var ph = ctx.Phase(0.1, 1.0);
Rgb c0 = ctx.Color(0), c1 = ctx.Color(1);
for (var i = 0; i < output.Length; i++)
{
    var a = Frac(ctx.U(i) - ph);
    var m = Saturate((MathF.Sin(MathF.Tau * a) * 2f) + 0.5f);
    output[i] = Lerp(c1, c0, m);
}""")

e('MopUp','Mop Up','扫除',1,7,True,True,'Trio',False,'Color fills the zone along u, then is wiped away in the same direction.',"""
var ph = ctx.Phase(0.1, 1.0);
var f = Frac(ph);
var c = ctx.Color(Floor(ph));
for (var i = 0; i < output.Length; i++)
{
    var u = ctx.U(i);
    var lit = f < 0.5f ? u <= 2f * f : u > (2f * f) - 1f;
    output[i] = lit ? c : Rgb.Black;
}""")

e('Lottery','Lottery','抽奖',0,7,True,False,'None',False,'One LED at a hashed position lights per step, leaving a dim afterglow on the previous pick.',"""
output.Clear();
var n = output.Length;
var s = Floor(ctx.T * ctx.Rate(2.0, 20.0));
output[RandInt(ctx.Seed, s - 1, 1, n)] = Scale(ctx.Color(s - 1), 0.3f);
output[RandInt(ctx.Seed, s, 1, n)] = ctx.Color(s);""")

e('Warning','Warning','警示',2,2,True,False,'Duo',True,'Police-style double strobe alternating between the two halves and colors.',"""
var ph = ctx.Phase(0.5, 2.0);
var active = (int)(Floor(ph) & 1);
var f = Frac(ph);
var strobe = f < 0.15f || (f >= 0.25f && f < 0.4f);
for (var i = 0; i < output.Length; i++)
{
    var half = ctx.U(i) < 0.5f ? 0 : 1;
    output[i] = strobe && half == active ? ctx.Color(half) : Rgb.Black;
}""")

e('Mixing','Mixing','混合',2,2,True,True,'Duo',False,'Two colors blend sinusoidally along u with a drifting mix point.',"""
var ph = Frac(ctx.Phase(0.05, 0.5));
Rgb c0 = ctx.Color(0), c1 = ctx.Color(1);
for (var i = 0; i < output.Length; i++)
{
    output[i] = Lerp(c0, c1, 0.5f + (0.5f * MathF.Sin(MathF.Tau * (ctx.U(i) - ph))));
}""")

e('Tide','Tide','海流',2,2,True,True,'[new Rgb(0, 64, 255), new Rgb(0, 255, 200)]',True,'Ocean current: two superposed slow waves modulate a two-color gradient.',"""
var ph = ctx.Phase(0.05, 0.6);
var p1 = Frac(ph);
var p2 = Frac((0.7 * ph) + 0.25);
Rgb c0 = ctx.Color(0), c1 = ctx.Color(1);
for (var i = 0; i < output.Length; i++)
{
    var u = ctx.U(i);
    var v = Saturate(0.5f + (0.3f * MathF.Sin(MathF.Tau * (u - p1))) + (0.2f * MathF.Sin(MathF.Tau * ((2.3f * u) + p2))));
    output[i] = Scale(Lerp(c1, c0, v), 0.4f + (0.6f * v));
}""")

e('Scan','Scan','扫描',1,7,True,False,'Trio',False,'A soft bar bounces end to end (Larson scanner); color changes per round trip.',"""
var ph = ctx.Phase(0.2, 2.0);
var pos = Tri(Frac(ph));
var c = ctx.Color(Floor(ph));
for (var i = 0; i < output.Length; i++)
{
    output[i] = Scale(c, Hat(ctx.U(i), pos, 0.12f));
}""")

e('DoubleMeteor','Double Meteor','双流星',2,2,True,False,'Duo',False,'Two meteors start at opposite ends and cross each other.',"""
var ph = ctx.Phase(0.2, 2.0);
const float tail = 0.3f;
var head = Frac(ph) * (1f + tail);
Rgb a = ctx.Color(0), b = ctx.Color(1);
for (var i = 0; i < output.Length; i++)
{
    var u = ctx.U(i);
    output[i] = AddSat(Scale(a, Tail(head - u, tail)), Scale(b, Tail(head - (1f - u), tail)));
}""")

e('MeteorContest','Meteor Contest','流星竞速',2,2,True,True,'Duo',False,'Two meteors race around the zone at different speeds.',"""
var ph = ctx.Phase(0.2, 2.0);
const float tail = 0.25f;
var h0 = Frac(ph);
var h1 = Frac((ph * 0.73) + 0.5);
Rgb a = ctx.Color(0), b = ctx.Color(1);
for (var i = 0; i < output.Length; i++)
{
    var u = ctx.U(i);
    output[i] = AddSat(Scale(a, Tail(Frac(h0 - u), tail)), Scale(b, Tail(Frac(h1 - u), tail)));
}""")

e('MeteorMix','Meteor Mix','流星混合',0,0,True,True,'None',False,'A looping meteor whose tail is painted with a rainbow.',"""
var ph = ctx.Phase(0.2, 2.0);
var head = Frac(ph);
var shift = 0.1f * Frac(ph * 0.1);
const float tail = 0.5f;
for (var i = 0; i < output.Length; i++)
{
    var d = Frac(head - ctx.U(i));
    var v = Tail(d, tail);
    output[i] = v > 0f ? Scale(ColorTables.RainbowAt((d * 2f) + shift), v) : Rgb.Black;
}""")

e('ReturnArc','Return Arc','回弧',1,7,True,True,'Trio',False,'An arc grows from the start to the end, then retracts back to the start.',"""
var ph = ctx.Phase(0.1, 1.0);
var extent = Tri(Frac(ph));
var c = ctx.Color(Floor(ph));
for (var i = 0; i < output.Length; i++)
{
    output[i] = ctx.U(i) <= extent ? c : Rgb.Black;
}""")

e('DoubleArc','Double Arc','双弧',1,7,True,False,'Trio',False,'Two arcs grow outward from the centre, then shrink back.',"""
var ph = ctx.Phase(0.1, 1.0);
var extent = 0.5f * Tri(Frac(ph));
var c = ctx.Color(Floor(ph));
for (var i = 0; i < output.Length; i++)
{
    output[i] = MathF.Abs(ctx.U(i) - 0.5f) <= extent ? c : Rgb.Black;
}""")

e('HeartBeat','Heart Beat','心跳',1,7,True,False,'One',True,'Lub-dub double pulse of the whole zone; color advances per beat.',"""
var ph = ctx.Phase(0.6, 2.0);
var f = Frac(ph);
var env = Pulse(f, 0.08f, 0.05f) + (0.6f * Pulse(f, 0.3f, 0.05f));
output.Fill(Scale(ctx.Color(Floor(ph)), env));""")

e('HeartBeatRunway','Heart Beat Runway','心跳跑道',1,7,True,True,'One',False,'The heartbeat envelope travels along u as a wave.',"""
var ph = ctx.Phase(0.6, 2.0);
var c = ctx.Color(0);
for (var i = 0; i < output.Length; i++)
{
    var f = Frac(ph - (0.5 * ctx.U(i)));
    output[i] = Scale(c, Pulse(f, 0.08f, 0.05f) + (0.6f * Pulse(f, 0.3f, 0.05f)));
}""")

e('Disco','Disco','迪斯科',0,7,True,False,'None',True,'Every beat each pair of LEDs jumps to a hashed palette/rainbow color.',"""
var b = Floor(ctx.T * ctx.Rate(1.0, 8.0));
for (var i = 0; i < output.Length; i++)
{
    output[i] = ctx.Color(RandInt(ctx.Seed, b, i >> 1, 7));
}""")

e('ElectricCurrent','Electric Current','电流',1,1,True,True,'[new Rgb(80, 120, 255)]',False,'Three flickering sparks race along a dim glowing wire.',"""
var ph = ctx.Phase(0.5, 4.0);
var c = ctx.Color(0);
var flickerStep = Floor(ctx.T * 30.0);
output.Fill(Scale(c, 0.08f));
for (var j = 0; j < 3; j++)
{
    var head = Frac(ph + (j / 3.0));
    var flicker = 0.5f + (0.5f * Rand(ctx.Seed, flickerStep, j));
    for (var i = 0; i < output.Length; i++)
    {
        var v = Tail(Frac(head - ctx.U(i)), 0.08f, 1f) * flicker;
        if (v > 0f)
        {
            output[i] = AddSat(output[i], Scale(Lerp(c, Rgb.White, v * 0.6f), v));
        }
    }
}""")

e('GradientRibbon','Gradient Ribbon','渐变缎带',0,7,True,True,'Trio',False,'A smooth gradient through the palette (rainbow if empty) scrolls along u.',"""
var ph = ctx.Phase(0.02, 0.4);
var cnt = Math.Max(ctx.Colors.Length, 2);
for (var i = 0; i < output.Length; i++)
{
    var x = Frac((0.5 * ctx.U(i)) - ph);
    if (ctx.Colors.Length == 0)
    {
        output[i] = ColorTables.RainbowAt(x);
        continue;
    }

    var p = x * cnt;
    var k = (long)p;
    output[i] = Lerp(ctx.Color(k), ctx.Color(k + 1), p - k);
}""")

e('Drumming','Drumming','击鼓',1,7,True,False,'Trio',False,'On every beat a flash hits the centre and a ring expands outward and fades.',"""
var ph = ctx.Phase(0.8, 3.0);
var f = Frac(ph);
var c = ctx.Color(Floor(ph));
var r = f * 0.6f;
var flash = f < 0.1f ? 0.3f * (1f - (f / 0.1f)) : 0f;
for (var i = 0; i < output.Length; i++)
{
    var d = MathF.Abs(ctx.U(i) - 0.5f);
    output[i] = Scale(c, (Hat(d, r, 0.12f) * (1f - f)) + flash);
}""")

e('CandyBox','Candy Box','糖果盒',0,7,True,True,'None',False,'Stripes of hashed palette colors scroll along u.',"""
var ph = ctx.Phase(0.05, 0.5);
for (var i = 0; i < output.Length; i++)
{
    var s = Floor((ctx.U(i) - ph) * 6.0);
    output[i] = ctx.Color(RandInt(ctx.Seed, s, 11, 7));
}""")

e('Burst','Burst','爆破',0,7,True,False,'None',True,'Explosions at hashed centres: a flash plus an expanding, fading shock ring.',"""
var ph = ctx.Phase(0.3, 2.0);
var ep = Floor(ph);
var f = Frac(ph);
var centre = Rand(ctx.Seed, ep, 1);
var c = ctx.Color(ep);
var r = f * 0.7f;
for (var i = 0; i < output.Length; i++)
{
    var d = MathF.Abs(ctx.U(i) - centre);
    var core = f < 0.15f ? Hat(d, 0f, 0.1f) * (1f - (f / 0.15f)) : 0f;
    output[i] = Scale(c, (Hat(d, r, 0.15f) * (1f - f)) + core);
}""")

e('Ripple','Ripple','涟漪',0,7,True,False,'None',True,'Two overlapping ripples from hashed centres, each with a main and a trailing ring.',"""
output.Clear();
var rate = ctx.Rate(0.3, 2.0);
for (var j = 0; j < 2; j++)
{
    var ph = (ctx.T * rate) + (0.5 * j);
    var ep = Floor(ph);
    var f = Frac(ph);
    var centre = Rand(ctx.Seed, (ep * 2) + j, 2);
    var c = ctx.Color((ep * 2) + j);
    var r = f * 0.8f;
    for (var i = 0; i < output.Length; i++)
    {
        var d = MathF.Abs(ctx.U(i) - centre);
        var v = (Hat(d, r, 0.1f) + (0.5f * Hat(d, r - 0.15f, 0.08f))) * (1f - f);
        if (v > 0f)
        {
            output[i] = AddSat(output[i], Scale(c, v));
        }
    }
}""")

e('Stacking','Stacking','堆叠',1,7,True,True,'One',True,'Dots run to the far end and stack up one by one; the full stack then fades out.',"""
var n = output.Length;
var cycle = (n * (n + 1L) / 2) + n;
var s = Floor(ctx.T * ctx.Rate(5.0, 60.0)) % cycle;
var stacked = n;
var dot = -1;
for (var h = 0; h < n; h++)
{
    var run = n - h;
    if (s < run)
    {
        stacked = h;
        dot = (int)s;
        break;
    }

    s -= run;
}

var fade = dot < 0 ? 1f - ((float)s / n) : 1f;
for (var i = 0; i < n; i++)
{
    var p = ctx.Index(i);
    if (p >= n - stacked)
    {
        output[i] = Scale(ctx.Color(n - 1 - p), fade);
    }
    else
    {
        output[i] = p == dot ? ctx.Color(stacked) : Rgb.Black;
    }
}""")

e('Echo','Echo','回声',1,7,True,True,'Trio',True,'A pulse crosses the zone followed by three decaying echoes.',"""
var ph = ctx.Phase(0.2, 1.5);
var head = Frac(ph) * 1.6f;
var c = ctx.Color(Floor(ph));
for (var i = 0; i < output.Length; i++)
{
    var u = ctx.U(i);
    var v = 0f;
    var amp = 1f;
    for (var k = 0; k < 4; k++)
    {
        v += amp * Hat(u, head - (0.18f * k), 0.05f);
        amp *= 0.6f;
    }

    output[i] = Scale(c, v);
}""")

assert len(E) == 34, len(E)
assert sum(1 for v in E.values() if v[7]) == 19

# ILoopable periods (ARCHITECTURE-D2 §1), derived from each body: 'one' = Frac(ph) only, 'color' = ctx.Color(Floor(ph)),
# 'n' = n phase cycles (Warning alternates halves: 2; MeteorMix/Tide carry a 0.1/0.7 sub-phase: 10), 'static' = time-invariant.
# Absent = random or very long period (MeteorContest's 0.73 ratio repeats after 100 cycles): fixed seeded fallback loop.
LOOP = {
    'StaticColor': 'static',
    'Rainbow': ('one', 0.05, 1.0), 'RainbowMorph': ('one', 0.02, 0.5), 'Mixing': ('one', 0.05, 0.5), 'Taichi': ('one', 0.1, 1.0),
    'Wave': ('one', 0.1, 1.0), 'DoubleMeteor': ('one', 0.2, 2.0), 'HeartBeatRunway': ('one', 0.6, 2.0), 'GradientRibbon': ('one', 0.02, 0.4),
    'Breathing': ('color', 0.1, 1.0), 'ColorCycle': ('color', 0.1, 1.0), 'CoverCycle': ('color', 0.1, 1.0), 'DoubleArc': ('color', 0.1, 1.0),
    'Drumming': ('color', 0.8, 3.0), 'Echo': ('color', 0.2, 1.5), 'HeartBeat': ('color', 0.6, 2.0), 'Meteor': ('color', 0.2, 2.0),
    'MopUp': ('color', 0.1, 1.0), 'ReturnArc': ('color', 0.1, 1.0), 'Runway': ('color', 0.1, 1.5), 'Scan': ('color', 0.2, 2.0),
    'Warning': (2, 0.5, 2.0), 'MeteorMix': (10, 0.2, 2.0), 'Tide': (10, 0.05, 0.6),
}

def loop_member(name):
    l = LOOP.get(name)
    if l is None:
        return '', ''
    if l == 'static':
        expr = '0'
    elif l[0] == 'one':
        expr = f'LoopPeriod.Cycles(p, {l[1]}, {l[2]}, 1)'
    elif l[0] == 'color':
        expr = f'LoopPeriod.PerColor(p, {l[1]}, {l[2]})'
    else:
        expr = f'LoopPeriod.Cycles(p, {l[1]}, {l[2]}, {l[0]})'
    return ', ILoopable', f"""

    public double LoopSeconds(in EffectParams p) => {expr};"""

TEMPLATE = """// <auto-generated>tools/codegen/gen_effects.py</auto-generated>
using UnifiedRgb.Core.Color;
using UnifiedRgb.Devices;
using static UnifiedRgb.Core.Effects.EffectMath;

namespace UnifiedRgb.Core.Effects.Generators;

/// <summary>{local} {display}: {doc}</summary>
public sealed class {name}Effect : EffectGenerator{iface}
{{
    public const string EffectId = "{name}";

    public {name}Effect()
        : base(EffectId, new EffectParamSchema("{display}", "{local}", {mn}, {mx}, {speed}, {dirn}, {defaults}, {w19}))
    {{
    }}

    public override void Render(in RenderContext ctx, Span<Rgb> output)
    {{
{body}
    }}{loop}
}}
"""
different = []
for name, (display, local, mn, mx, speed, dirn, defaults, w19, doc, body) in E.items():
    if not defaults.startswith('['):
        defaults = 'EffectMath.' + defaults
    b = textwrap.indent(body.strip('\n'), ' ' * 8)
    iface, loop = loop_member(name)
    src = TEMPLATE.format(iface=iface, loop=loop, name=name, display=display, local=local, mn=mn, mx=mx, speed=str(speed).lower(),
                          dirn=str(dirn).lower(), defaults=defaults, w19=str(w19).lower(), doc=doc, body=b)
    target = os.path.join(OUT, name + 'Effect.cs')
    if args.check:
        try:
            with open(target, encoding='utf-8') as f:
                actual = f.read()
        except FileNotFoundError:
            actual = None
        if actual != src:
            different.append(target)
    else:
        with open(target, 'w', encoding='utf-8', newline='\n') as f:
            f.write(src)
if different:
    raise SystemExit('Generated effect source differs:\n' + '\n'.join(different))
print('verified' if args.check else 'wrote', len(E))
