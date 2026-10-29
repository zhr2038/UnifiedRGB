using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;
using UnifiedRgb.Core.Clock;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Engine;
using UnifiedRgb.Devices;
using UnifiedRgb.Devices.AsusRog;
using UnifiedRgb.Host.Services;
namespace UnifiedRgb.Host.Tests;
[Collection("Host")]
public sealed class PeripheralRenderInventoryTests
{
 [Fact]
 public async Task Resume_and_release_refresh_the_actual_render_inventory_without_waiting_for_a_scan_change()
 {
  var gate=new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true,null));
  var o=new UnifiedRgbOptions {UseVirtualAdapter=false,DataDirectory=Path.Combine(Path.GetTempPath(),"urgb-render-inventory-"+Guid.NewGuid().ToString("N"))};
  var services=new ServiceCollection().AddLogging().AddUnifiedRgb(o,enableIpc:false);
  services.AddSingleton(gate);services.AddSingleton<IWriterGuard>(new Guard());services.AddSingleton<IDeviceAdapterFactory>(new Factory());
  await using var provider=services.BuildServiceProvider();var e=provider.GetRequiredService<LightingEngine>();
  var r=new RogPeripheralConfigurationResources(e,provider.GetRequiredService<KeyboardDisplayService>());
  await e.StartAsync(default);
  try {
   e.Loop.Controllers.Should().BeEmpty();gate.BeginFreshResume();await r.ResumeAsync(default);gate.EndFreshResume();gate.AllowRogAcquisition();
   e.Adapters.Controllers.Should().Contain(x=>x.Descriptor.Id.Value=="test:peripheral");
   e.Loop.Controllers.Should().Contain(x=>x.Id.Value=="test:peripheral");
   e.SetScene(e.DefaultScene());
   gate.BlockRogAcquisition();await r.ReleaseAsync(default);e.Loop.Controllers.Should().BeEmpty();
  } finally {await e.StopAsync(default);}
 }
 // Render lifecycle data path: the actual LightingEngine, AdapterHost, FrameBus and configured ZoneFollowers behind the actual
 // PeripheralConfigurationService/RogPeripheralConfigurationResources, with recording fake device sinks and a manual render clock.
 // Every colour is checked at the fake writer callbacks against the literal Steps table; waits are bounded handshakes, never sleeps.
 [Fact]
 public async Task Handoff_cycles_drive_fresh_peripheral_writers_with_the_current_case_led0_colour_and_retire_them_while_the_case_continues()
 {
  var data=Path.Combine(Path.GetTempPath(),"urgb-render-lifecycle-"+Guid.NewGuid().ToString("N"));
  var o=new UnifiedRgbOptions {UseVirtualAdapter=false,DataDirectory=data,ZoneFollowers=[Follow(Keyboard),Follow(Mouse)]};
  using var gate=new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true,null));
  var source=new LifecycleFactory(Case);var keyboard=new LifecycleFactory(Keyboard);var mouse=new LifecycleFactory(Mouse);
  var guard=new Guard();var clock=new ManualRenderClock();var notifier=new RgbNotifier();var profiles=new ProfileStore(o);var oled=new NoKeyboardTransport();
  using var bus=new FrameBus();
  var adapters=new AdapterHost([source,keyboard,mouse],null!,guard,bus,()=>new HybridSleeper(),NullLogger<AdapterHost>.Instance,o,gate);
  using var e=new LightingEngine(o,adapters,bus,EffectRegistry.Default,clock,profiles,new MountingStore(o),guard,notifier,NullLogger<LightingEngine>.Instance);
  var rgb=new RgbService(e,profiles,guard,notifier);
  using var keyboardDisplay=new KeyboardDisplayService(o,rgb,DispatchProxy.Create<IScreenService,CasePanelScreenRpc>(),oled,guard,configurationGate:gate);
  var service=new PeripheralConfigurationService(o,gate,new RogPeripheralConfigurationResources(e,keyboardDisplay));
  using var statusWriter=new StatusWriter(o,e,notifier,NullLogger<StatusWriter>.Instance);
  using var lights=new CasePowerCoordinator(rgb);
  Hold? pending=null;
  await e.StartAsync(default);
  try {
   var caseSink=source.Sinks.Should().ContainSingle().Subject;
   var step=0;var shown=Steps[0];
   // One tick of the actual render clock; returns once the case callback got the shown step and each follower its zone-0 colour.
   async Task TickAsync(params LifecycleSink[] followers)
   {
    var from=followers.Select(f=>f.Count).ToArray();var caseFrom=caseSink.Count;var (a,b)=(shown.Followed.Rgb,shown.Other.Rgb);
    clock.Advance();
    await caseSink.ShowsAsync(caseFrom,[[a,a,a],[b,b]]);
    for(var i=0;i<followers.Length;i++) await followers[i].ShowsAsync(from[i],[[a]]);
   }
   async Task PaintAsync(params LifecycleSink[] followers)
   {
    shown=Steps[step++];
    await rgb.SetSceneAsync(new([new([new(Case.Id.Value,0)],"StaticColor",[shown.Followed.Hex]),new([new(Case.Id.Value,1)],"StaticColor",[shown.Other.Hex])]));
    await TickAsync(followers);
   }

   // Persisted handoff at start: only the source case renders; no ROG adapter exists and neither follower has a target.
   (await service.GetPeripheralConfigurationStatusAsync()).State.Should().Be("Ready");
   keyboard.Sinks.Should().BeEmpty();mouse.Sinks.Should().BeEmpty();
   Ids(e.Loop.Controllers).Should().Equal(Case.Id.Value);
   e.Loop.FollowerStatus.Select(f=>f.State).Should().Equal("MissingController","MissingController");
   await PaintAsync();
   for(var cycle=1;cycle<=2;cycle++)
   {
    // Resume: new nonempty instances; the first frame either one ever receives is the latest case LED0 colour, which no
    // retired instance showed (every step colour is distinct).
    (await service.ExitPeripheralConfigurationAsync()).State.Should().Be("Active");
    keyboard.Sinks.Should().HaveCount(cycle);mouse.Sinks.Should().HaveCount(cycle);
    var kb=keyboard.Sinks[^1];var ms=mouse.Sinks[^1];
    kb.Opens.Should().Be(1);ms.Opens.Should().Be(1);kb.Count.Should().Be(0);ms.Count.Should().Be(0);
    await TickAsync(kb,ms);
    kb.Writes[0].Colors.Should().Equal(shown.Followed.Rgb);ms.Writes[0].Colors.Should().Equal(shown.Followed.Rgb);
    Ids(e.Loop.Controllers).Should().BeEquivalentTo(Case.Id.Value,Keyboard.Id.Value,Mouse.Id.Value);
    e.Loop.FollowerStatus.Select(f=>f.State).Should().Equal("Ready","Ready");
    await PaintAsync(kb,ms);await PaintAsync(kb,ms);

    // Enter, held at the first ROG adapter disposal: both target writers are already stopped and joined, their entries not yet
    // removed. Status reads there succeed, and the case keeps rendering the current colour.
    pending=new Hold();kb.DisposeHold=pending;ms.DisposeHold=pending;
    var entering=Task.Run(()=>service.EnterPeripheralConfigurationAsync());
    await pending.Entered.WaitAsync(Bound);
    (await service.GetPeripheralConfigurationStatusAsync()).State.Should().Be("Entering");
    kb.WriterThread!.IsAlive.Should().BeFalse();ms.WriterThread!.IsAlive.Should().BeFalse();
    var retired=(kb.Count,ms.Count);
    e.BuildStatus(diagnostics:false).Devices.ToDictionary(d=>d.Id,d=>d.State).Should().BeEquivalentTo(new Dictionary<string,string>
     {[Case.Id.Value]=ControllerStates.Active,[Keyboard.Id.Value]=ControllerStates.Offline,[Mouse.Id.Value]=ControllerStates.Offline});
    (await rgb.ListControllersAsync()).Should().HaveCount(3);(await rgb.GetStatusAsync()).Devices.Should().HaveCount(3);
    e.ReadTelemetry().Should().BeEmpty();
    statusWriter.WriteNow();File.Exists(statusWriter.StatusPath).Should().BeTrue();
    await PaintAsync();
    pending.Release();pending=null;
    (await entering).State.Should().Be("Ready");

    // Ready: the targets are retired from the render inventory and the bus; their callbacks stay silent while the case continues.
    kb.Disposed.Should().BeTrue();ms.Disposed.Should().BeTrue();
    bus[Keyboard.Id].Should().BeNull();bus[Mouse.Id].Should().BeNull();
    Ids(e.Loop.Controllers).Should().Equal(Case.Id.Value);
    e.Loop.FollowerStatus.Select(f=>f.State).Should().Equal("MissingController","MissingController");
    await PaintAsync();
    (kb.Count,ms.Count).Should().Be(retired);
   }

   // Third resume with the mouse asleep (open refused): the keyboard still follows, the mouse stays Offline with no writer, and the
   // active-only case blackout succeeds through the actual RgbService/LightingEngine, leaving the offline mouse out.
   mouse.Asleep=true;
   (await service.ExitPeripheralConfigurationAsync()).State.Should().Be("Active");
   keyboard.Sinks.Should().HaveCount(3);mouse.Sinks.Should().HaveCount(3);
   var kb3=keyboard.Sinks[^1];var ms3=mouse.Sinks[^1];
   ms3.Opens.Should().Be(1);
   (await rgb.ListControllersAsync()).ToDictionary(c=>c.Id,c=>c.State).Should().BeEquivalentTo(new Dictionary<string,string>
    {[Case.Id.Value]=ControllerStates.Active,[Keyboard.Id.Value]=ControllerStates.Active,[Mouse.Id.Value]=ControllerStates.Offline});
   await TickAsync(kb3);
   kb3.Writes[0].Colors.Should().Equal(shown.Followed.Rgb);
   e.Loop.FollowerStatus.Select(f=>f.State).Should().Equal("Ready","MissingController");
   await PaintAsync(kb3);
   (await lights.ExecuteAsync(CasePowerIntent.Blackout)).Changed.Should().BeTrue();
   (await rgb.GetSceneAsync()).Assignments.Should().ContainSingle().Which.Targets.Should()
    .BeEquivalentTo(new ZoneRefDto[] {new(Case.Id.Value,0),new(Case.Id.Value,1),new(Keyboard.Id.Value,0)});
   var black=new Rgb(0,0,0);var caseFrom=caseSink.Count;var kbFrom=kb3.Count;
   clock.Advance();
   await caseSink.ShowsAsync(caseFrom,[[black,black,black],[black,black]]);
   await kb3.ShowsAsync(kbFrom,[[black]]);
   ms3.Count.Should().Be(0);

   // No keyboard OLED transport was opened, the case adapter was never replaced, and no sink saw a write after its disposal, a
   // second writer thread, or a disposal before its writer was joined.
   oled.Opens.Should().Be(0);
   source.Sinks.Should().ContainSingle();
   source.Sinks.Concat(keyboard.Sinks).Concat(mouse.Sinks).SelectMany(s=>s.Violations).Should().BeEmpty();
  } finally {
   pending?.Release();
   await e.StopAsync(default);
   try {Directory.Delete(data,recursive:true);} catch (IOException) {}
  }
 }
 // Status reads while retirement clears ControllerEntry.Writer on its own thread (AdapterHost.DetachAsync, the ROG release), on the
 // same actual composition and fakes as the lifecycle test above. The reads are the actual LightingEngine.BuildStatus,
 // DtoMapper.ToDto and StatusWriter paths; counters are compared with the fake's recorded writes; waits are bounded handshakes.
 [Fact]
 public async Task Status_reads_racing_writer_retirement_report_one_writer_snapshot_and_never_fault_the_status_path()
 {
  var data=Path.Combine(Path.GetTempPath(),"urgb-status-retirement-"+Guid.NewGuid().ToString("N"));
  var o=new UnifiedRgbOptions {UseVirtualAdapter=false,DataDirectory=data,ZoneFollowers=[Follow(Keyboard),Follow(Mouse)]};
  using var gate=new PeripheralConfigurationGate(new PeripheralConfigurationMarker.LoadResult(true,null));
  var source=new LifecycleFactory(Case);var keyboard=new LifecycleFactory(Keyboard);var mouse=new LifecycleFactory(Mouse);
  var guard=new Guard();var clock=new ManualRenderClock();var notifier=new RgbNotifier();var profiles=new ProfileStore(o);
  using var bus=new FrameBus();
  var adapters=new AdapterHost([source,keyboard,mouse],null!,guard,bus,()=>new HybridSleeper(),NullLogger<AdapterHost>.Instance,o,gate);
  using var e=new LightingEngine(o,adapters,bus,EffectRegistry.Default,clock,profiles,new MountingStore(o),guard,notifier,NullLogger<LightingEngine>.Instance);
  var rgb=new RgbService(e,profiles,guard,notifier);
  using var keyboardDisplay=new KeyboardDisplayService(o,rgb,DispatchProxy.Create<IScreenService,CasePanelScreenRpc>(),new NoKeyboardTransport(),guard,configurationGate:gate);
  var service=new PeripheralConfigurationService(o,gate,new RogPeripheralConfigurationResources(e,keyboardDisplay));
  using var statusWriter=new StatusWriter(o,e,notifier,NullLogger<StatusWriter>.Instance);
  using var stopReading=new CancellationTokenSource();
  Hold? pending=null;
  await e.StartAsync(default);
  try {
   (await service.ExitPeripheralConfigurationAsync()).State.Should().Be("Active");
   var kb=keyboard.Sinks.Should().ContainSingle().Subject;var ms=mouse.Sinks.Should().ContainSingle().Subject;
   foreach(var step in Steps[..2])
   {
    await rgb.SetSceneAsync(new([new([new(Case.Id.Value,0)],"StaticColor",[step.Followed.Hex]),new([new(Case.Id.Value,1)],"StaticColor",[step.Other.Hex])]));
    var (k,m)=(kb.Count,ms.Count);clock.Advance();await kb.ShowsAsync(k,[[step.Followed.Rgb]]);await ms.ShowsAsync(m,[[step.Followed.Rgb]]);
   }

   // Retirement starts by quiescing (ReleaseForPeripheralConfigurationAsync): the writer stays attached and paused and its write in
   // flight has finished, so every status path reports Contended with exactly the writes the fake recorded; resumed, Active.
   var entry=adapters.Controllers.Single(c=>c.Descriptor.Id==Keyboard.Id);var writer=entry.Writer!;
   await writer.QuiesceAsync();
   const long written=2;kb.Count.Should().Be((int)written);
   var expected=new {State=ControllerStates.Contended,BlockedBy=(string?)null,MaxFps=1000,Written=written,Dropped=0L,LastError=(string?)null};
   KeyboardStatus(e.BuildStatus(diagnostics:false)).Should().BeEquivalentTo(expected);
   DtoMapper.ToDto(entry).State.Should().Be(ControllerStates.Contended);
   statusWriter.WriteNow();KeyboardStatus(await ReadStatusAsync(statusWriter)).Should().BeEquivalentTo(expected);
   writer.Paused=false;
   KeyboardStatus(e.BuildStatus(diagnostics:false)).Should().BeEquivalentTo(expected with {State=ControllerStates.Active});
   DtoMapper.ToDto(entry).State.Should().Be(ControllerStates.Active);

   // Retirement clears entry.Writer at any instant and attach sets it. The test thread performs exactly those field writes on the
   // actual entry until a reader thread has gone through BuildStatus, the mapper and StatusWriter 20000 times and has seen both
   // states. Nothing renders meanwhile: a snapshot with the writer carries its exact counters, one without it is Offline.
   var (reads,live,retired)=(0,0,0);var torn=new List<string>();
   var reader=Task.Factory.StartNew(()=>
   {
    var bound=Stopwatch.StartNew();
    while((reads<20_000||live==0||retired==0)&&!stopReading.IsCancellationRequested&&bound.Elapsed<Bound)
    {
     var s=KeyboardStatus(e.BuildStatus(diagnostics:false));var m=DtoMapper.ToDto(entry).State;
     if(s is {State:ControllerStates.Active,Written:written,Dropped:0,LastError:null}) live++;
     else if(s is {State:ControllerStates.Offline,Written:0,BlockedBy:null}) retired++;
     else torn.Add($"status {s.State} written={s.Written}");
     if(m is not (ControllerStates.Active or ControllerStates.Offline)) torn.Add($"mapper {m}");
     if(++reads%500==0) statusWriter.WriteNow();
    }
   },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
   try {
    var bound=Stopwatch.StartNew();
    while(!reader.IsCompleted&&bound.Elapsed<Bound+Bound) {entry.Writer=null;Thread.SpinWait(1);entry.Writer=writer;Thread.SpinWait(1);}
   } finally {entry.Writer=writer;}
   await reader.WaitAsync(Bound);
   torn.Should().BeEmpty();reads.Should().BeGreaterThanOrEqualTo(20_000);live.Should().BePositive();retired.Should().BePositive();

   // Actual retirement with a reader running throughout. Enter is held at the first ROG adapter disposal, where both writers are
   // already cleared, until the reader has read status there; each peripheral's observed state only moves forward.
   var order=new[] {ControllerStates.Active,ControllerStates.Contended,ControllerStates.Offline,"absent"};
   var seen=new Dictionary<string,List<string>> {[Keyboard.Id.Value]=[],[Mouse.Id.Value]=[]};
   var count=0;
   pending=new Hold();kb.DisposeHold=pending;ms.DisposeHold=pending;
   var retiring=Task.Factory.StartNew(()=>
   {
    var bound=Stopwatch.StartNew();
    while(!stopReading.IsCancellationRequested&&bound.Elapsed<Bound)
    {
     var devices=e.BuildStatus(diagnostics:false).Devices;
     foreach(var c in adapters.Controllers) DtoMapper.ToDto(c);
     foreach(var (id,states) in seen)
     {
      var state=devices.FirstOrDefault(d=>d.Id==id)?.State??"absent";
      if(states.Count==0||states[^1]!=state) states.Add(state);
     }
     if(Interlocked.Increment(ref count)%500==0) statusWriter.WriteNow();
    }
   },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
   async Task ReadAsync()
   {
    var target=Volatile.Read(ref count)+10;
    SpinWait.SpinUntil(()=>Volatile.Read(ref count)>=target||retiring.IsCompleted,Bound);
    if(retiring.IsCompleted) await retiring;
    Volatile.Read(ref count).Should().BeGreaterThanOrEqualTo(target);
   }
   await ReadAsync();
   var entering=Task.Run(()=>service.EnterPeripheralConfigurationAsync());
   await pending.Entered.WaitAsync(Bound);
   await ReadAsync();
   pending.Release();pending=null;
   (await entering).State.Should().Be("Ready");
   await ReadAsync();
   await stopReading.CancelAsync();await retiring.WaitAsync(Bound);
   foreach(var (id,states) in seen)
   {
    states.Should().StartWith(ControllerStates.Active,"{0} was live",id).And.EndWith("absent").And.Contain(ControllerStates.Offline);
    states.Select(s=>Array.IndexOf(order,s)).Should().NotContain(-1).And.BeInAscendingOrder("{0} retires once, in order",id);
   }
   statusWriter.WriteNow();
   (await ReadStatusAsync(statusWriter)).Devices.Select(d=>(d.Id,d.State)).Should().Equal((Case.Id.Value,ControllerStates.Active));
   kb.Disposed.Should().BeTrue();ms.Disposed.Should().BeTrue();
   source.Sinks.Concat(keyboard.Sinks).Concat(mouse.Sinks).SelectMany(s=>s.Violations).Should().BeEmpty();
  } finally {
   pending?.Release();
   await stopReading.CancelAsync();
   await e.StopAsync(default);
   try {Directory.Delete(data,recursive:true);} catch (IOException) {}
  }
 }
 private static DeviceStatusDto KeyboardStatus(StatusDto status)=>status.Devices.Single(d=>d.Id==Keyboard.Id.Value);
 private static readonly JsonSerializerOptions StatusJson=ContractsJson.Configure(new JsonSerializerOptions());
 private static async Task<StatusDto> ReadStatusAsync(StatusWriter writer)=>
  JsonSerializer.Deserialize<StatusDto>(await StatusWriter.ReadSharedAsync(writer.StatusPath),StatusJson)!;
 private static readonly TimeSpan Bound=TimeSpan.FromSeconds(10);
 private static readonly ControllerDescriptor Case=new(new("test:case"),"test","fake case",DeviceType.Ledstrip,DeviceClass.Virtual,"",
  [new(new(0),"followed",ZoneLayout.Linear(3)),new(new(1),"other",ZoneLayout.Linear(2))],Capabilities.DirectOnly(1000));
 private static readonly ControllerDescriptor Keyboard=new(new("test:keyboard"),"test","fake keyboard",DeviceType.Keyboard,DeviceClass.AsusRog,"",
  [new(new(0),"aggregate",ZoneLayout.Linear(1))],Capabilities.DirectOnly(1000));
 private static readonly ControllerDescriptor Mouse=new(new("test:mouse"),"test","fake mouse",DeviceType.Mouse,DeviceClass.AsusRog,"",
  [new(new(0),"aggregate",ZoneLayout.Linear(1))],Capabilities.DirectOnly(1000));
 // Literal oracle: the hex each step asks for on case zone 0 (whose LED0 both peripherals follow) and on zone 1, with the exact
 // bytes the writer callbacks must receive. All twenty colours are distinct, so a write identifies its step and its zone.
 private static readonly ((string Hex,Rgb Rgb) Followed,(string Hex,Rgb Rgb) Other)[] Steps=
 [
  (("#C81E0A",new(0xC8,0x1E,0x0A)),("#0A0B0C",new(0x0A,0x0B,0x0C))),
  (("#0A64C8",new(0x0A,0x64,0xC8)),("#203040",new(0x20,0x30,0x40))),
  (("#11EE77",new(0x11,0xEE,0x77)),("#405060",new(0x40,0x50,0x60))),
  (("#FF8000",new(0xFF,0x80,0x00)),("#607080",new(0x60,0x70,0x80))),
  (("#7F00FF",new(0x7F,0x00,0xFF)),("#8090A0",new(0x80,0x90,0xA0))),
  (("#00FFFF",new(0x00,0xFF,0xFF)),("#A0B0C0",new(0xA0,0xB0,0xC0))),
  (("#123456",new(0x12,0x34,0x56)),("#C0D0E0",new(0xC0,0xD0,0xE0))),
  (("#FEDCBA",new(0xFE,0xDC,0xBA)),("#E0F0FF",new(0xE0,0xF0,0xFF))),
  (("#01FE02",new(0x01,0xFE,0x02)),("#0F1F2F",new(0x0F,0x1F,0x2F))),
  (("#AB01CD",new(0xAB,0x01,0xCD)),("#3F4F5F",new(0x3F,0x4F,0x5F))),
 ];
 private static ZoneFollowerOptions Follow(ControllerDescriptor target)=>new()
  {SourceControllerId=Case.Id.Value,SourceZoneId=0,SourceLed=0,TargetControllerId=target.Id.Value,TargetZoneId=0};
 private static string[] Ids(IEnumerable<ControllerDescriptor> controllers)=>controllers.Select(c=>c.Id.Value).ToArray();
 private sealed class ManualRenderClock:IClock
 {
  private bool _running;
  public double Now{get;private set;} public long Frame{get;private set;} public event Action<double,double>? Tick;
  public void Start(int fps)=>_running=true; public void Stop()=>_running=false;
  public void Advance()
  {
   if(!_running) throw new InvalidOperationException("The engine has not started the render clock.");
   Frame++;Now=Frame/60.0;Tick?.Invoke(Now,1/60.0);
  }
 }
 // Each Create is a new adapter instance (generation = creation order) for the same descriptor; Asleep makes the next ones refuse open.
 private sealed class LifecycleFactory(ControllerDescriptor d):IDeviceAdapterFactory
 {
  public DeviceClass Class=>d.Class;public List<LifecycleSink> Sinks{get;}=[];public bool Asleep{get;set;}
  public IDeviceAdapter Create(IServiceProvider s){var sink=new LifecycleSink(d,Sinks.Count+1,Asleep);Sinks.Add(sink);return sink;}
 }
 // Records every writer callback with its colours, plus lifecycle violations; ShowsAsync is the bounded write handshake.
 private sealed class LifecycleSink(ControllerDescriptor d,int generation,bool asleep):IDeviceAdapter,IDisposable
 {
  private readonly List<(int Zone,Rgb[] Colors)> _writes=[];
  private readonly List<string> _violations=[];
  private TaskCompletionSource _changed=new(TaskCreationOptions.RunContinuationsAsynchronously);
  private Thread? _writer;
  private int _inFlight;
  private bool _disposed;
  public int Opens{get;private set;}
  public Hold? DisposeHold{get;set;}
  public int Count{get{lock(_writes) return _writes.Count;}}
  public (int Zone,Rgb[] Colors)[] Writes{get{lock(_writes) return _writes.ToArray();}}
  public IReadOnlyList<string> Violations{get{lock(_writes) return _violations.ToArray();}}
  public Thread? WriterThread{get{lock(_writes) return _writer;}}
  public bool Disposed{get{lock(_writes) return _disposed;}}
  public string Vendor=>"test";public DeviceClass Class=>d.Class;public event Action<HotplugEvent>? Hotplug{add{} remove{}}
  public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ControllerDescriptor>>([d]);
  public Task OpenAsync(ControllerId id,CancellationToken ct)
  {
   Opens++;
   if(asleep) throw new IOException($"{d.Id.Value} #{generation} is asleep (fake).");
   return Task.CompletedTask;
  }
  public void WriteFrame(ControllerId id,ZoneId z,ReadOnlySpan<Rgb> colors)
  {
   Interlocked.Increment(ref _inFlight);
   try {
    lock(_writes)
    {
     if(_disposed) _violations.Add($"{d.Id.Value} #{generation}: write after disposal");
     if(id!=d.Id) _violations.Add($"{d.Id.Value} #{generation}: write for {id.Value}");
     if(_writer is null) _writer=Thread.CurrentThread;
     else if(_writer!=Thread.CurrentThread) _violations.Add($"{d.Id.Value} #{generation}: second writer thread");
     _writes.Add((z.Value,colors.ToArray()));
     var changed=_changed;_changed=new(TaskCreationOptions.RunContinuationsAsynchronously);changed.TrySetResult();
    }
   } finally {Interlocked.Decrement(ref _inFlight);}
  }
  public async Task ShowsAsync(int from,Rgb[][] frame)
  {
   using var bound=new CancellationTokenSource(Bound);
   while(true)
   {
    Task changed;
    lock(_writes)
    {
     for(var i=from;i+frame.Length<=_writes.Count;i++)
      if(Enumerable.Range(0,frame.Length).All(z=>_writes[i+z].Zone==d.Zones[z].Id.Value&&_writes[i+z].Colors.SequenceEqual(frame[z]))) return;
     changed=_changed.Task;
    }
    try {await changed.WaitAsync(bound.Token);}
    catch (OperationCanceledException)
    {
     throw new TimeoutException($"{d.Id.Value} #{generation} never received [{Describe(frame)}]; writes since #{from}: "
      +string.Join(" ",Writes.Skip(from).Select(w=>$"z{w.Zone}[{string.Join(",",w.Colors)}]")));
    }
   }
  }
  private static string Describe(Rgb[][] frame)=>string.Join(" | ",frame.Select(z=>string.Join(",",z)));
  public Task SetFirmwareModeAsync(ControllerId id,FirmwareMode m,CancellationToken ct)=>throw new NotSupportedException();
  public Telemetry? ReadTelemetry(ControllerId id)=>null;public Task CloseAsync(ControllerId id)=>Task.CompletedTask;
  public void Dispose()
  {
   lock(_writes)
   {
    if(Volatile.Read(ref _inFlight)!=0||_writer is {IsAlive:true}) _violations.Add($"{d.Id.Value} #{generation}: disposed before its writer was joined");
    _disposed=true;
   }
   DisposeHold?.Wait();
  }
 }
 // Holds a disposal on the retiring thread until the test has read status; the first holder signals Entered.
 private sealed class Hold
 {
  private readonly TaskCompletionSource _entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly TaskCompletionSource _released=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public Task Entered=>_entered.Task;
  public void Wait()
  {
   _entered.TrySetResult();
   if(!_released.Task.Wait(Bound)) throw new TimeoutException("A held ROG adapter disposal was never released.");
  }
  public void Release()=>_released.TrySetResult();
 }
 // The keyboard display is the actual service with no configured route; any transport open would be recorded and refused.
 private sealed class NoKeyboardTransport:IRogWiredOledFactory
 {
  public int Opens{get;private set;}
  public RogWiredOledConnection? TryOpen(string deviceKey){Opens++;throw new InvalidOperationException("No keyboard transport may be opened in this test.");}
 }
 private sealed class Factory:IDeviceAdapterFactory {public DeviceClass Class=>DeviceClass.AsusRog;public IDeviceAdapter Create(IServiceProvider s)=>new Adapter();}
 private sealed class Adapter:IDeviceAdapter,IDisposable
 {
  private readonly ControllerDescriptor d=new(new("test:peripheral"),"test","fake mouse",DeviceType.Mouse,DeviceClass.AsusRog,"",[new(new(0),"fake",ZoneLayout.Linear(1))],Capabilities.DirectOnly(10));
  public string Vendor=>"test";public DeviceClass Class=>DeviceClass.AsusRog;public event Action<HotplugEvent>? Hotplug{add{} remove{}}
  public Task<IReadOnlyList<ControllerDescriptor>> DiscoverAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ControllerDescriptor>>([d]);
  public Task OpenAsync(ControllerId id,CancellationToken ct)=>Task.CompletedTask;
  public void WriteFrame(ControllerId id,ZoneId z,ReadOnlySpan<Rgb> colors){}
  public Task SetFirmwareModeAsync(ControllerId id,FirmwareMode m,CancellationToken ct)=>throw new NotSupportedException();
  public Telemetry? ReadTelemetry(ControllerId id)=>null;public Task CloseAsync(ControllerId id)=>Task.CompletedTask;public void Dispose(){}
 }
 private sealed class Guard:IWriterGuard
 {
  public GuardVerdict Check(DeviceClass c)=>GuardVerdict.Clear;public bool IsTakeoverEnabled(DeviceClass c)=>false;
  public Task<TakeoverResult> SetTakeoverAsync(DeviceClass c,bool e,CancellationToken ct)=>Task.FromResult(new TakeoverResult(c,e,[],[],null));
  public Task<TakeoverResult> TakeoverAsync(DeviceClass c,CancellationToken ct)=>Task.FromResult(new TakeoverResult(c,true,[],[],null));
  public Task RestoreAsync(CancellationToken ct)=>Task.CompletedTask;
 }
}