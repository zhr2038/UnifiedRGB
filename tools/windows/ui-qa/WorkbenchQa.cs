using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnifiedRgb.App.Views;
using UnifiedRgb.App.Workbench;
using UnifiedRgb.Contracts;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.UiQa;

internal static class WorkbenchQa
{
    public static void Run(IReadOnlyDictionary<string, string> options)
    {
        string Get(string k, string fallback) => options.GetValueOrDefault(k, fallback);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UnifiedRgb.App;component/Themes/StudioTheme.xaml", UriKind.Relative) });
        var fake = new EditorFake(options.GetValueOrDefault("--source-zip")); var errors = new List<string>();
        var vm = new ScenePackagesViewModel(() => fake, () => Task.FromResult<IReadOnlyList<ControllerDto>>([]), () => Task.FromResult<IReadOnlyList<ScreenDeviceDto>>([]), () => null, ex => errors.Add(ex.ToString()), _ => true);
        var view = new ScenePackagesView { DataContext = vm, Margin = new Thickness(18) };
        var window = new Window { Title = "光域 · 黑金工作台验收 · 模拟内容", Content = view, Width = double.Parse(Get("--width-dip", "1060")), Height = double.Parse(Get("--height-dip", "540")),
            Background = Brushes.Black, Foreground = Brushes.White, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var record = Path.GetFullPath(Get("--record", "workbench-qa.json")); Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; File.WriteAllText(record + ".error.txt", e.Exception.ToString()); };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await vm.RefreshAsync(); vm.Selected = vm.Packages[0]; await vm.ReadSelectionAsync();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (vm.PreviewData?.Tracks.Count != 9 || !vm.EditorAvailable) throw new InvalidOperationException("Editor/preview not loaded.");
                var reads = 0; var sample = new PcTelemetryDto(DateTimeOffset.UtcNow, 21, null, null, 34, 52, null, null, "QA", null);
                var sampleReady = new TaskCompletionSource<PcTelemetryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.ReadPreviewTelemetry = () => { reads++; return sampleReady.Task; };
                var pendingSample = vm.RefreshPreviewTelemetryAsync(); await vm.RefreshPreviewTelemetryAsync();
                sampleReady.SetResult(sample); await pendingSample; await vm.RefreshPreviewTelemetryAsync();
                if (reads != 1 || vm.TelemetrySnapshot != sample) throw new InvalidOperationException("Preview telemetry was not coalesced independently of the screen page.");
                vm.PreviewPlaying = false; vm.PreviewPositionMs = 0; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var first = Raster(view); vm.PreviewPositionMs = 5400; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var second = Raster(view); if (first.SequenceEqual(second)) throw new InvalidOperationException("Dynamic preview did not change.");
                vm.LightingBrightness = 38; vm.HueDegrees = 120; vm.PlaybackSpeed = 2; vm.PlaybackReverse = true;
                await vm.SaveEditsCommand.ExecuteAsync();
                if (fake.Saves != 1 || fake.Applies != 0 || vm.EditorDirty) throw new InvalidOperationException("Save/apply boundary failed.");
                vm.UpdateInputErrors(view, 1); if (vm.CanCommitEdits) throw new InvalidOperationException("Invalid input allowed apply."); vm.UpdateInputErrors(view, 0);
                vm.VariantName = "测试 · 金色流动"; await vm.SaveVariantCommand.ExecuteAsync();
                if (fake.Variants != 1 || fake.Applies != 0) throw new InvalidOperationException("Save variant applied.");
                await vm.ApplyEditsCommand.ExecuteAsync(); if (fake.Applies != 1) throw new InvalidOperationException("Explicit apply not routed.");
                vm.LightingBrightness = 100; vm.HueDegrees = 0; vm.PlaybackSpeed = 1; vm.PlaybackReverse = false; vm.PreviewPositionMs = 3200;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var controls = Descendants(view).OfType<Button>().Where(b => b.IsVisible).Select(b => new { Label = b.Content as string, b.IsEnabled, b.ActualWidth, b.ActualHeight }).ToArray();
                if (errors.Count != 0 || controls.Any(b => b.ActualWidth <= 0 || b.ActualHeight <= 0)) throw new InvalidOperationException("Invalid visible controls or UI error.");
                File.WriteAllBytes(record + ".diagnostic-layout.png", Raster(view));
                File.WriteAllText(record, JsonSerializer.Serialize(new { Dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX, Window = new { window.ActualWidth, window.ActualHeight }, Page = new { view.ActualWidth, view.ActualHeight }, fake.Saves, fake.Variants, fake.Applies,
                    PreviewTracks = vm.PreviewData.Tracks.Count, LogicalLights = vm.PreviewData.Tracks.Sum(t => t.Info.LedCount), DifferentPreviewFrames = true, Controls = controls, Errors = errors }, new JsonSerializerOptions { WriteIndented = true }));
                vm.PreviewPlaying = true;
                if (Get("--exit-after-check", "false") == "true") app.Shutdown();
            }
            catch (Exception ex) { File.WriteAllText(record + ".failed.txt", ex.ToString()); Environment.ExitCode = 1; app.Shutdown(); }
        };
        window.Closed += (_, _) => vm.Dispose(); app.Run(window);
    }
    private static byte[] Raster(FrameworkElement view)
    {
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); png.Save(stream); return stream.ToArray();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var next in Descendants(child)) yield return next; } }

    private sealed class EditorFake : IScenePackageService
    {
        private static readonly string Id = new('1', 64);
        private readonly Dictionary<string, byte[]> _files = [];
        private readonly ScenePackagePreviewDto _preview;
        private readonly ScenePackageDetailsDto _details;
        private readonly string? _zipPath;
        private ScenePackageEditorStateDto _editor;
        public int Saves, Applies, Variants;
        public EditorFake(string? zipPath)
        {
            _zipPath = zipPath;
            var roles = new[] { "top-fans", "side-fans", "bottom-fans", "waterblock-ring", "ram-inner", "ram-outer", "motherboard-logo", "gpu-front", "gpu-top" };
            var counts = new[] { 78,78,78,24,8,8,2,12,4 }; var labels = new[] { "顶排风扇", "侧排风扇", "底排风扇", "冷头光环", "内存一", "内存二", "主板标志", "显卡双空档竖灯", "显卡水平灯" };
            var tracks = new List<ScenePackagePreviewTrackDto>(); var rows = new List<ScenePackageRoleDto>();
            for (var role = 0; role < roles.Length; role++)
            {
                var n = counts[role]; var bytes = new byte[400*n*3];
                for (var f=0;f<400;f++) for(var i=0;i<n;i++) { var v=(Math.Sin(2*Math.PI*(i/(double)n-f/200.0))+1)/2;var o=(f*n+i)*3; bytes[o]=(byte)(40+215*v);bytes[o+1]=(byte)(25+160*v);bytes[o+2]=(byte)(10+70*v); }
                _files[roles[role]] = bytes; tracks.Add(new(roles[role],labels[role],roles[role],n,400,40,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
                rows.Add(new(roles[role],labels[role],"lighting",n,0,0,400,16000,["#D8B578"]));
            }
            var info = new ScenePackageInfoDto(Id,"black-gold-qa","鎏金流动 · 全机联动",1,"rgb24-hold-physical-ms-v1",16000,9,0);
            _details = new(info,rows); _preview=new(Id,info.Name,16000,tracks,[]);
            _editor = new(Id,new string('2',64),new(),roles.Select(r=>new ScenePackageBindingDto(r)).ToArray(),true,[],[]);
            if (zipPath is not null)
            {
                using var zip = ZipFile.OpenRead(zipPath); using var manifestStream = zip.GetEntry("scene.json")!.Open();
                using var doc = JsonDocument.Parse(manifestStream); var m = doc.RootElement; var hashes = m.GetProperty("files");
                var duration = m.GetProperty("durationMs").GetDouble(); tracks.Clear(); rows.Clear(); _files.Clear();
                foreach (var l in m.GetProperty("lighting").EnumerateArray())
                {
                    var role = l.GetProperty("role").GetString()!; var file=l.GetProperty("file").GetString()!; var n=l.GetProperty("ledCount").GetInt32(); var f=l.GetProperty("frames").GetInt32();
                    using var input=zip.GetEntry(file)!.Open();using var output=new MemoryStream();input.CopyTo(output);_files[file]=output.ToArray();
                    tracks.Add(new(role,role,file,n,f,l.GetProperty("frameDurationMs").GetDouble(),_files[file].Length,hashes.GetProperty(file).GetString()!));
                    rows.Add(new(role,role,"lighting",n,0,0,f,duration,["#D8B578"]));
                }
                var screens = new List<ScenePackagePreviewScreenDto>();
                foreach(var s in m.GetProperty("screens").EnumerateArray())
                {
                    var role=s.GetProperty("role").GetString()!; var width=role=="main-panel"?1920:480;
                    var clips=s.TryGetProperty("segments",out var segs)?segs.EnumerateArray().Select(c=>new ScenePackagePreviewClipDto(c.GetProperty("file").GetString()!,c.GetProperty("durationMs").GetDouble(),zip.GetEntry(c.GetProperty("file").GetString()!)!.Length,hashes.GetProperty(c.GetProperty("file").GetString()!).GetString()!)).ToArray():[new ScenePackagePreviewClipDto(s.GetProperty("file").GetString()!,s.GetProperty("durationMs").GetDouble(),0,hashes.GetProperty(s.GetProperty("file").GetString()!).GetString()!)];
                    screens.Add(new(role,role,width,480,s.GetProperty("fps").GetInt32(),s.GetProperty("overlay").GetString()!,s.TryGetProperty("accentColor",out var accent)?accent.GetString()!:"#D8B578",s.GetProperty("fit").GetString()!,s.TryGetProperty("overlayLayout",out var region)?JsonSerializer.Deserialize<ScreenOverlayLayoutDto>(region.GetRawText(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true}):null,clips));
                    rows.Add(new(role,role,"screen",0,width,480,0,duration,[]));
                }
                info=info with {Name=m.GetProperty("name").GetString()!,DurationMs=duration,LightingRoles=tracks.Count,ScreenRoles=screens.Count,SchemaVersion=m.GetProperty("schemaVersion").GetInt32()};
                _details=new(info,rows.ToArray());_preview=new(Id,info.Name,duration,tracks.ToArray(),screens);
                _editor=_editor with {Bindings=rows.Select(r=>new ScenePackageBindingDto(r.Role)).ToArray()};
            }
        }
        public Task<IReadOnlyList<ScenePackageInfoDto>> ListScenePackagesAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<ScenePackageInfoDto>>([_details.Info]);
        public Task<ScenePackageDetailsDto> GetScenePackageAsync(string id,CancellationToken ct=default)=>Task.FromResult(_details);
        public Task<ScenePackageBindingStateDto> GetScenePackageBindingsAsync(string id,CancellationToken ct=default)=>Task.FromResult(new ScenePackageBindingStateDto(Id,_editor.BindingId,_editor.Bindings,true,[]));
        public Task<ScenePackageEditorStateDto> GetScenePackageEditorAsync(string id,string? bindingId=null,CancellationToken ct=default)=>Task.FromResult(_editor);
        public Task<ScenePackagePreviewDto> GetScenePackagePreviewAsync(string id,CancellationToken ct=default)=>Task.FromResult(_preview);
        public Task<ScenePackagePreviewChunkDto> ReadScenePackagePreviewChunkAsync(ScenePackagePreviewChunkRequestDto r,CancellationToken ct=default)
        {
            if(!_files.TryGetValue(r.File,out var bytes)){using var zip=ZipFile.OpenRead(_zipPath!);using var input=zip.GetEntry(r.File)!.Open();using var output=new MemoryStream();input.CopyTo(output);bytes=output.ToArray();_files[r.File]=bytes;}
            var sha=_preview.Lighting.FirstOrDefault(t=>t.File==r.File)?.Sha256??_preview.Screens.SelectMany(s=>s.Clips).First(c=>c.File==r.File).Sha256;
            return Task.FromResult(new ScenePackagePreviewChunkDto(r.Offset,bytes.Length,sha,Convert.ToBase64String(bytes,(int)r.Offset,(int)Math.Min(r.Bytes,bytes.Length-r.Offset))));
        }
        public Task<ScenePackageEditorStateDto> SaveScenePackageEditAsync(ScenePackageEditRequestDto r,CancellationToken ct=default)
        {Saves++;_editor=_editor with {BindingId=new string('3',64),Playback=r.Playback,Versions=[new(new string('3',64),DateTimeOffset.UtcNow,r.Playback)]};return Task.FromResult(_editor);}
        public Task SaveScenePackageVariantAsync(ScenePackageSaveVariantDto r,CancellationToken ct=default){Variants++;return Task.CompletedTask;}
        public Task<ScenePackageApplyDto> ApplyScenePackageCheckedAsync(ScenePackageApplyRequestDto r,CancellationToken ct=default){Applies++;return Task.FromResult(new ScenePackageApplyDto(true,Id,r.ExpectedBindingId,"已应用模拟内容"));}
        public Task<ScenePackageApplyDto> ApplyScenePackageAsync(string id,CancellationToken ct=default)=>throw new InvalidOperationException("Unchecked apply");
        public Task<ScenePackageDetailsDto> ImportScenePackageAsync(ScenePackageUploadDto p,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<ScenePackageBindingStateDto> SaveScenePackageBindingsAsync(ScenePackageBindingsDto b,CancellationToken ct=default)=>throw new NotSupportedException();
    }
}
