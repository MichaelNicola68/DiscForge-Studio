using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using DiscForgeStudio.Models;

namespace DiscForgeStudio.Services;

public sealed class IsoBurnService
{
    private static void Diagnostic(string message) { }

    private readonly IsoPreflightService _preflight = new();
    private readonly IsoHashService _hash = new();

    public Task<IsoBurnResult> BurnAsync(IsoBurnOptions options, IProgress<IsoProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<IsoBurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Diagnostic("IMAPI STA worker started.");
                var result = BurnOnStaAsync(options, progress, cancellationToken).GetAwaiter().GetResult();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                Diagnostic("IMAPI STA worker failed: " + ex);
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "DiscForge IMAPI STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private async Task<IsoBurnResult> BurnOnStaAsync(IsoBurnOptions options, IProgress<IsoProgress>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
Diagnostic($"START | ISO={options.IsoPath} | Recorder={options.RecorderUniqueId}");
        Diagnostic($"Progress-Empfänger vorhanden: {progress is not null}");
        var errors = _preflight.ValidateBurn(options);
        if (errors.Count > 0) return new(false, options.IsoPath, 0, null, started, DateTimeOffset.Now, string.Join(Environment.NewLine, errors));

        var iso = Path.GetFullPath(options.IsoPath);
        var bytes = new FileInfo(iso).Length;
        progress?.Report(new("ISO prüfen", 0, 0, bytes, Path.GetFileName(iso)));
        var inspection = options.CalculateImageSha256 ? _hash.InspectAsync(iso, cancellationToken).GetAwaiter().GetResult() : null;
        progress?.Report(new("Laufwerk initialisieren", 2, 0, bytes));

        dynamic? recorder = null;
        dynamic? writer = null;
        IStream? sourceStream = null;
        ProgressReadStream? progressStream = null;
        BurnEventSink? eventSink = null;
        Action<object, object>? eventHandler = null;
        var eventId = new Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recorderType = Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")
                ?? throw new PlatformNotSupportedException("IMAPI2 Recorder ist nicht verfügbar.");
            recorder = Activator.CreateInstance(recorderType)!;
            recorder.InitializeDiscRecorder(options.RecorderUniqueId);
            Diagnostic("Recorder initialisiert.");

            var writerType = Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")
                ?? throw new PlatformNotSupportedException("IMAPI2 Data Writer ist nicht verfügbar.");
            writer = Activator.CreateInstance(writerType)!;
            writer.Recorder = recorder;
            writer.ClientName = "DiscForgeStudio";
            writer.ForceMediaToBeClosed = options.CloseMedia;
            Diagnostic("MsftDiscFormat2Data konfiguriert.");

            eventSink = new BurnEventSink(bytes, progress, cancellationToken);
            eventHandler = (sender, args) => { _ = eventSink.Update(sender, args); };
            Diagnostic($"ComEventsHelper.Combine | IID={eventId} | DISPID=512.");
            ComEventsHelper.Combine(writer, eventId, 0x200, eventHandler);
            Diagnostic("ComEventsHelper.Combine successful.");

            progress?.Report(new("Medium prüfen", 3, 0, bytes));
            if (!(bool)writer.IsCurrentMediaSupported(recorder))
                throw new InvalidOperationException("Das eingelegte Medium wird für Datenaufzeichnung nicht unterstützt.");
            if (!(bool)writer.IsRecorderSupported(recorder))
                throw new InvalidOperationException("Das ausgewählte Laufwerk wird nicht unterstützt.");

            long freeBytes = (long)writer.FreeSectorsOnMedia * 2048L;
            if (bytes > freeBytes)
                throw new InvalidOperationException("Die ISO-Datei passt nicht auf das eingelegte Medium.");

            sourceStream = OpenReadStream(iso);
            progressStream = new ProgressReadStream(sourceStream, bytes, progress, cancellationToken);
            progress?.Report(new("Schreibvorgang starten", 5, 0, bytes));

            writer.Write(progressStream);

            progress?.Report(new("Medium abschließen", 96, bytes, bytes));
            if (options.EjectAfterWrite)
            {
                progress?.Report(new("Medium auswerfen", 99, bytes, bytes));
                recorder.EjectMedia();
            }
            progress?.Report(new("Fertig", 100, bytes, bytes));
            Diagnostic("ERGEBNIS: Erfolgreich.");
            return new(true, iso, bytes, inspection?.Sha256, started, DateTimeOffset.Now, "ISO wurde erfolgreich auf das Medium geschrieben.");
        }
        catch (OperationCanceledException)
        {
            try { if (writer is not null) writer.CancelWrite(); } catch { }
            return new(false, iso, bytes, inspection?.Sha256, started, DateTimeOffset.Now, "ISO-Brennvorgang wurde abgebrochen.");
        }
        catch (Exception ex)
        {
            Diagnostic($"FEHLER | {ex.GetType().FullName} | HRESULT=0x{ex.HResult:X8} | {ex.GetBaseException().Message}");
            return new(false, iso, bytes, inspection?.Sha256, started, DateTimeOffset.Now, ex.GetBaseException().Message);
        }
        finally
        {
            if (writer is not null && eventHandler is not null)
            {
                try
                {
                    ComEventsHelper.Remove(writer, eventId, 0x200, eventHandler);
                    Diagnostic("ComEventsHelper.Remove successful.");
                }
                catch (Exception ex)
                {
                    Diagnostic("ComEventsHelper.Remove failed: " + ex.Message);
                }
            }
            GC.KeepAlive(eventHandler);
            GC.KeepAlive(eventSink);
            progressStream?.Dispose();
            ComRelease.Final(sourceStream);
            ComRelease.Final(writer);
            ComRelease.Final(recorder);
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateStreamOnFileEx(string fileName, uint mode, uint attributes, bool create, IStream? template, out IStream stream);

    private static IStream OpenReadStream(string path)
    {
        const uint STGM_READ = 0;
        const uint STGM_SHARE_DENY_WRITE = 0x20;
        var hr = SHCreateStreamOnFileEx(path, STGM_READ | STGM_SHARE_DENY_WRITE, 0, false, null, out var stream);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        return stream;
    }

    private sealed class ProgressReadStream : IStream, IDisposable
    {
        private readonly IStream _inner;
        private readonly long _total;
        private readonly IProgress<IsoProgress>? _progress;
        private readonly CancellationToken _token;
        private long _processed;
        private int _lastPercent = -1;
        private long _lastReportTicks;

        public ProgressReadStream(IStream inner, long total, IProgress<IsoProgress>? progress, CancellationToken token)
        {
            _inner = inner;
            _total = Math.Max(1, total);
            _progress = progress;
            _token = token;
        }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            _token.ThrowIfCancellationRequested();
            _inner.Read(pv, cb, pcbRead);
            var read = pcbRead == IntPtr.Zero ? cb : Marshal.ReadInt32(pcbRead);
            if (read <= 0) return;
            ReportProgress(read);
        }

        private void ReportProgress(int bytesRead)
        {
            if (bytesRead <= 0) return;
            _processed = Math.Min(_total, _processed + bytesRead);
            var raw = (int)(_processed * 100L / _total);
            var percent = 5 + (int)(raw * 90L / 100L);
            var now = Environment.TickCount64;
            if (percent != _lastPercent || now - _lastReportTicks >= 500)
            {
                _lastPercent = percent;
                _lastReportTicks = now;
                _progress?.Report(new("ISO schreiben", percent, _processed, _total));
            }
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            _inner.Seek(dlibMove, dwOrigin, plibNewPosition);
            if (plibNewPosition != IntPtr.Zero) _processed = Math.Clamp(Marshal.ReadInt64(plibNewPosition), 0, _total);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag) => _inner.Stat(out pstatstg, grfStatFlag);
        public void Clone(out IStream ppstm) => _inner.Clone(out ppstm);
        public void Commit(int grfCommitFlags) => _inner.Commit(grfCommitFlags);
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            _token.ThrowIfCancellationRequested();
            const int bufferSize = 256 * 1024;
            var buffer = new byte[bufferSize];
            var readPtr = Marshal.AllocCoTaskMem(sizeof(int));
            var writtenPtr = Marshal.AllocCoTaskMem(sizeof(int));
            long totalRead = 0;
            long totalWritten = 0;
            try
            {
                while (totalRead < cb)
                {
                    _token.ThrowIfCancellationRequested();
                    var request = (int)Math.Min(buffer.Length, cb - totalRead);
                    _inner.Read(buffer, request, readPtr);
                    var read = Marshal.ReadInt32(readPtr);
                    if (read <= 0) break;

                    pstm.Write(buffer, read, writtenPtr);
                    var written = Marshal.ReadInt32(writtenPtr);
                    if (written != read) throw new EndOfStreamException($"IMAPI-Ausgabestrom schrieb nur {written} von {read} Bytes.");

                    totalRead += read;
                    totalWritten += written;
                    ReportProgress(read);
                }

                if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, totalRead);
                if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, totalWritten);
            }
            finally
            {
                Marshal.FreeCoTaskMem(readPtr);
                Marshal.FreeCoTaskMem(writtenPtr);
            }
        }
        public void LockRegion(long libOffset, long cb, int dwLockType) => _inner.LockRegion(libOffset, cb, dwLockType);
        public void Revert() => _inner.Revert();
        public void SetSize(long libNewSize) => _inner.SetSize(libNewSize);
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => _inner.UnlockRegion(libOffset, cb, dwLockType);
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException("Der ISO-Eingabestrom ist schreibgeschützt.");
        public void Dispose() { }
    }
    [ComImport]
    [Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface DDiscFormat2DataEvents
    {
        [DispId(0x200)]
        [PreserveSig]
        int Update([MarshalAs(UnmanagedType.IDispatch)] object writer, [MarshalAs(UnmanagedType.IDispatch)] object progress);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class BurnEventSink : DDiscFormat2DataEvents
    {
        private readonly long _isoBytes;
        private readonly IProgress<IsoProgress>? _progress;
        private readonly CancellationToken _token;
        private int _eventCount;
        public int EventCount => Volatile.Read(ref _eventCount);

        public BurnEventSink(long isoBytes, IProgress<IsoProgress>? progress, CancellationToken token)
        {
            _isoBytes = Math.Max(1, isoBytes);
            _progress = progress;
            _token = token;
        }

        public int Update(object writerObject, object progressObject)
        {
            var call = Interlocked.Increment(ref _eventCount);
            Diagnostic($"EVENT #{call} empfangen | progressType={progressObject?.GetType().FullName ?? "null"}");
            try
            {
                if (_token.IsCancellationRequested)
                {
                    try { ((dynamic)writerObject).CancelWrite(); } catch { }
                    return 0;
                }

                ArgumentNullException.ThrowIfNull(progressObject);
                dynamic args = progressObject;
                int action = Convert.ToInt32(args.CurrentAction);
                long startLba = SafeLong(() => Convert.ToInt64(args.StartLba));
                long sectorCount = SafeLong(() => Convert.ToInt64(args.SectorCount));
                long lastWrittenLba = SafeLong(() => Convert.ToInt64(args.LastWrittenLba));
                long writtenSectors = sectorCount > 0 ? Math.Clamp(lastWrittenLba - startLba + 1, 0, sectorCount) : 0;
                long writtenBytes = Math.Min(_isoBytes, writtenSectors * 2048L);
                Diagnostic($"EVENT #{call} Werte | Action={action} | StartLba={startLba} | LastWrittenLba={lastWrittenLba} | SectorCount={sectorCount} | Bytes={writtenBytes}");

                string phase;
                int percent;
                switch (action)
                {
                    case 0: phase = "Medium validieren"; percent = 4; break;
                    case 1: phase = "Medium formatieren"; percent = 5; break;
                    case 2: phase = "Hardware initialisieren"; percent = 6; break;
                    case 3: phase = "Laser kalibrieren"; percent = 7; break;
                    case 4:
                        phase = "ISO schreiben";
                        var fraction = sectorCount <= 0 ? 0d : (double)writtenSectors / sectorCount;
                        percent = 8 + (int)Math.Round(Math.Clamp(fraction, 0d, 1d) * 84d);
                        break;
                    case 5: phase = "Medium abschließen"; percent = 94; writtenBytes = _isoBytes; break;
                    case 6: phase = "Schreibvorgang abgeschlossen"; percent = 99; writtenBytes = _isoBytes; break;
                    case 7: phase = "Gebrannte Daten prüfen"; percent = 98; writtenBytes = _isoBytes; break;
                    default: phase = "Brennvorgang"; percent = 5; break;
                }

                _progress?.Report(new(phase, Math.Clamp(percent, 0, 99), writtenBytes, _isoBytes));
                Diagnostic($"EVENT #{call} an UI gemeldet | Phase={phase} | Prozent={percent}");
            }
            catch (Exception ex)
            {
                Diagnostic($"EVENT #{call} Auswertung fehlgeschlagen | {ex.GetType().Name} | {ex.Message}");
            }
            return 0;
        }

        private static long SafeLong(Func<long> getter)
        {
            try { return getter(); } catch { return 0; }
        }
    }

}
