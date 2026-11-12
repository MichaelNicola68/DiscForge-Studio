using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class BurnService
{
    private object? _activeWriter;

    public void Cancel()
    {
        var writer = _activeWriter;
        if (writer is null) return;
        try { ((dynamic)writer).CancelWrite(); } catch { }
    }

    public async Task<string> ProbeMediaAsync(string driveName, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            dynamic? recorder = null;
            dynamic? writer = null;
            try
            {
                recorder = ResolveRecorder(driveName);
                writer = CreateWriter(recorder, closeMedia: false);
                if (!(bool)writer.IsCurrentMediaSupported(recorder))
                    throw new InvalidOperationException("Das eingelegte Medium wird vom Laufwerk nicht unterstützt oder ist noch nicht bereit.");
                if (!(bool)writer.MediaHeuristicallyBlank)
                    throw new InvalidOperationException("Das Medium ist nicht leer. Für eine neue Daten-Disc wird ein leeres Medium benötigt.");
                completion.TrySetResult($"Leeres beschreibbares Medium erkannt. IMAPI-Medientyp: {(int)writer.CurrentPhysicalMediaType}");
            }
            catch (Exception ex) { completion.TrySetException(ex.GetBaseException()); }
            finally
            {
                ReleaseCom(writer);
                ReleaseCom(recorder);
            }
        }) { IsBackground = true, Name = "DiscForge Probe STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        return await completion.Task.ConfigureAwait(false);
    }

    public async Task BurnAsync(
        string driveName,
        IReadOnlyList<CompilationItem> items,
        string label,
        bool closeMedia,
        bool eject,
        IProgress<(int Percent, string Message)> progress,
        CancellationToken token)
    {
        if (items.Count == 0) throw new InvalidOperationException("Die Datei-Zusammenstellung ist leer.");

        var work = Path.Combine(Path.GetTempPath(), "DF", Guid.NewGuid().ToString("N")[..8]);
        var staging = Path.Combine(work, "src");
        Directory.CreateDirectory(staging);
        var sourceBytes = items.Sum(item => Math.Max(0, item.Size));

        try
        {
            await CopyToStagingAsync(items, staging, sourceBytes, progress, token).ConfigureAwait(false);
            await RunStaBurnAsync(driveName, staging, label, closeMedia, eject, sourceBytes, progress, token).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static async Task CopyToStagingAsync(
        IReadOnlyList<CompilationItem> items,
        string staging,
        long totalBytes,
        IProgress<(int Percent, string Message)> progress,
        CancellationToken token)
    {
        long copied = 0;
        var lastReport = 0L;
        progress.Report((2, "Vorbereitung · Temporäre Disc-Struktur wird erstellt"));

        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            var relative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var target = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using var source = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                copied += read;
                var now = Environment.TickCount64;
                if (now - lastReport >= 250 || copied >= totalBytes)
                {
                    lastReport = now;
                    var fraction = totalBytes <= 0 ? 1d : Math.Clamp((double)copied / totalBytes, 0d, 1d);
                    var percent = 2 + (int)Math.Round(fraction * 16d);
                    progress.Report((percent, $"Vorbereitung · {FormatBytes(copied)} von {FormatBytes(totalBytes)} · {item.RelativePath}"));
                }
            }
        }
    }

    private Task RunStaBurnAsync(
        string driveName,
        string staging,
        string label,
        bool closeMedia,
        bool eject,
        long sourceBytes,
        IProgress<(int Percent, string Message)> progress,
        CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            dynamic? recorder = null;
            dynamic? writer = null;
            dynamic? image = null;
            dynamic? imageResult = null;
            Action<object, object>? eventHandler = null;
            BurnEventSink? eventSink = null;
            var eventId = new Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E");

            try
            {
                token.ThrowIfCancellationRequested();
                progress.Report((20, "Laufwerk wird initialisiert"));
                recorder = ResolveRecorder(driveName);
                writer = CreateWriter(recorder, closeMedia);
                _activeWriter = writer;

                if (!(bool)writer.IsRecorderSupported(recorder))
                    throw new InvalidOperationException("Das ausgewählte Laufwerk wird nicht unterstützt.");
                if (!(bool)writer.IsCurrentMediaSupported(recorder))
                    throw new InvalidOperationException("Das eingelegte Medium wird nicht unterstützt.");
                if (!(bool)writer.MediaHeuristicallyBlank)
                    throw new InvalidOperationException("Das Medium ist nicht leer.");

                progress.Report((23, "Dateisystem-Abbild wird erzeugt"));
                var imageType = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage")
                    ?? throw new PlatformNotSupportedException("IMAPI2 Dateisystemabbild ist nicht verfügbar.");
                image = Activator.CreateInstance(imageType)!;
                image.ChooseImageDefaults(recorder);
                image.FileSystemsToCreate = 7;
                image.VolumeName = string.IsNullOrWhiteSpace(label) ? "DISCFORGE" : label;
                image.Root.AddTree(Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar), false);
                imageResult = image.CreateResultImage();

                eventSink = new BurnEventSink(sourceBytes, progress, token);
                eventHandler = (sender, args) => eventSink.Update(sender, args);
                ComEventsHelper.Combine(writer, eventId, 0x200, eventHandler);

                progress.Report((30, $"Schreibvorgang startet · 0 B von {FormatBytes(sourceBytes)}"));
                writer.Write(imageResult.ImageStream);
                progress.Report((96, $"Medium wird abgeschlossen · {FormatBytes(sourceBytes)} von {FormatBytes(sourceBytes)}"));

                if (eject)
                {
                    progress.Report((99, "Medium wird ausgeworfen"));
                    recorder.EjectMedia();
                }

                progress.Report((100, $"Fertig · {FormatBytes(sourceBytes)} erfolgreich geschrieben"));
                completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                try { if (writer is not null) writer.CancelWrite(); } catch { }
                completion.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex.GetBaseException());
            }
            finally
            {
                _activeWriter = null;
                if (writer is not null && eventHandler is not null)
                {
                    try { ComEventsHelper.Remove(writer, eventId, 0x200, eventHandler); } catch { }
                }
                GC.KeepAlive(eventHandler);
                GC.KeepAlive(eventSink);
                ReleaseCom(imageResult);
                ReleaseCom(image);
                ReleaseCom(writer);
                ReleaseCom(recorder);
            }
        }) { IsBackground = true, Name = "DiscForge Data Burn STA" };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static dynamic ResolveRecorder(string driveName)
    {
        dynamic? master = null;
        try
        {
            var masterType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
                ?? throw new PlatformNotSupportedException("IMAPI2 DiscMaster ist nicht verfügbar.");
            master = Activator.CreateInstance(masterType)!;
            var wanted = driveName.Trim().TrimEnd('\\').TrimEnd(':').ToUpperInvariant();
            dynamic? onlyRecorder = null;

            for (var index = 0; index < (int)master.Count; index++)
            {
                var uniqueId = (string)master.Item(index);
                var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!;
                dynamic recorder = Activator.CreateInstance(recorderType)!;
                recorder.InitializeDiscRecorder(uniqueId);
                if ((int)master.Count == 1) onlyRecorder = recorder;
                foreach (var path in (IEnumerable)recorder.VolumePathNames)
                {
                    var actual = path?.ToString()?.Trim().TrimEnd('\\').TrimEnd(':').ToUpperInvariant();
                    if (actual == wanted) return recorder;
                }
                if ((int)master.Count != 1) ReleaseCom(recorder);
            }

            if (onlyRecorder is not null) return onlyRecorder;
            throw new InvalidOperationException($"Das optische Laufwerk {driveName} wurde von IMAPI2 nicht gefunden.");
        }
        finally { ReleaseCom(master); }
    }

    private static dynamic CreateWriter(dynamic recorder, bool closeMedia)
    {
        var writerType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")
            ?? throw new PlatformNotSupportedException("IMAPI2 Data Writer ist nicht verfügbar.");
        dynamic writer = Activator.CreateInstance(writerType)!;
        writer.Recorder = recorder;
        writer.ClientName = "DiscForgeStudio";
        writer.ForceMediaToBeClosed = closeMedia;
        return writer;
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }

    private static string FormatBytes(long value)
    {
        if (value >= 1024L * 1024 * 1024) return $"{value / 1024d / 1024 / 1024:N2} GB";
        if (value >= 1024L * 1024) return $"{value / 1024d / 1024:N1} MB";
        if (value >= 1024L) return $"{value / 1024d:N1} KB";
        return $"{value:N0} B";
    }

    private sealed class BurnEventSink
    {
        private readonly long _totalBytes;
        private readonly IProgress<(int Percent, string Message)> _progress;
        private readonly CancellationToken _token;

        public BurnEventSink(long totalBytes, IProgress<(int Percent, string Message)> progress, CancellationToken token)
        {
            _totalBytes = Math.Max(1, totalBytes);
            _progress = progress;
            _token = token;
        }

        public void Update(object writerObject, object progressObject)
        {
            try
            {
                if (_token.IsCancellationRequested)
                {
                    try { ((dynamic)writerObject).CancelWrite(); } catch { }
                    return;
                }

                dynamic args = progressObject;
                var action = Convert.ToInt32(args.CurrentAction);
                var startLba = SafeLong(() => Convert.ToInt64(args.StartLba));
                var sectorCount = SafeLong(() => Convert.ToInt64(args.SectorCount));
                var lastWrittenLba = SafeLong(() => Convert.ToInt64(args.LastWrittenLba));
                var writtenSectors = sectorCount > 0 ? Math.Clamp(lastWrittenLba - startLba + 1, 0, sectorCount) : 0;
                var writtenBytes = Math.Min(_totalBytes, writtenSectors * 2048L);

                string phase;
                int percent;
                switch (action)
                {
                    case 0: phase = "Medium wird validiert"; percent = 26; break;
                    case 1: phase = "Medium wird formatiert"; percent = 27; break;
                    case 2: phase = "Hardware wird initialisiert"; percent = 28; break;
                    case 3: phase = "Laser wird kalibriert"; percent = 29; break;
                    case 4:
                        phase = "Daten werden geschrieben";
                        var fraction = sectorCount <= 0 ? 0d : (double)writtenSectors / sectorCount;
                        percent = 30 + (int)Math.Round(Math.Clamp(fraction, 0d, 1d) * 62d);
                        break;
                    case 5: phase = "Medium wird abgeschlossen"; percent = 96; writtenBytes = _totalBytes; break;
                    case 6: phase = "Schreibvorgang abgeschlossen"; percent = 99; writtenBytes = _totalBytes; break;
                    case 7: phase = "Gebrannte Daten werden geprüft"; percent = 98; writtenBytes = _totalBytes; break;
                    default: phase = "Brennvorgang läuft"; percent = 30; break;
                }

                var detail = writtenBytes > 0
                    ? $"{phase} · {FormatBytes(writtenBytes)} von {FormatBytes(_totalBytes)}"
                    : phase;
                _progress.Report((Math.Clamp(percent, 0, 99), detail));
            }
            catch { }
        }

        private static long SafeLong(Func<long> getter)
        {
            try { return getter(); } catch { return 0; }
        }
    }
}
