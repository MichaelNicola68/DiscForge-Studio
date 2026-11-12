using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using DiscForgeStudio.Models;
namespace DiscForgeStudio.Services;
public static class ExternalUpdaterLauncher
{
 public static void Launch(string packagePath, UpdatePackageManifest manifest)
 {
  var root=AppContext.BaseDirectory;
  var diagnosticDirectory=Path.Combine(root,"Update-Backups");Directory.CreateDirectory(diagnosticDirectory);
  var launcherLog=Path.Combine(diagnosticDirectory,"External-Updater-Launcher.log");
  var updater=Path.Combine(root,"Updater","DiscForgeUpdater.exe");
  var app=Environment.ProcessPath ?? string.Empty;
  File.WriteAllLines(launcherLog,new[]{"LAUNCH REQUEST",DateTime.Now.ToString("O"),"Profile="+(manifest.TestProfile??"<null>"),"Package="+packagePath,"Updater="+updater,"UpdaterExists="+File.Exists(updater),"App="+app,"Pid="+Environment.ProcessId},Encoding.UTF8);
  if(!string.Equals(manifest.TestProfile,"ExternalExecutableRollbackTest",StringComparison.Ordinal)) throw new InvalidOperationException("Paket nicht fuer externen Updater freigegeben.");
  if(string.IsNullOrWhiteSpace(app))throw new InvalidOperationException("Programmpfad fehlt.");
  if(!File.Exists(updater))throw new FileNotFoundException("Externer Updater fehlt.",updater);
  var psi=new ProcessStartInfo(updater){UseShellExecute=false,WorkingDirectory=root,CreateNoWindow=false};
  psi.ArgumentList.Add("--apply");psi.ArgumentList.Add(packagePath);psi.ArgumentList.Add(app);psi.ArgumentList.Add(Environment.ProcessId.ToString());
  File.AppendAllText(launcherLog,Environment.NewLine+"ProcessStartInfo ready"+Environment.NewLine,Encoding.UTF8);
  var updaterProcess=Process.Start(psi);
  if(updaterProcess is null)throw new InvalidOperationException("Updater konnte nicht gestartet werden.");
  File.AppendAllText(launcherLog,"UpdaterPid="+updaterProcess.Id+Environment.NewLine+"Shutdown requested"+Environment.NewLine,Encoding.UTF8);
  Application.Current.Shutdown();
 }
}
