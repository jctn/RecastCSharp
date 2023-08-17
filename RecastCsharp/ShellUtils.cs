using System;
using System.Diagnostics;
using System.Text;
using Debug = UnityEngine.Debug;

namespace RecastSharp
{
    public class ShellUtils
    {
        private const string ClientCommand = "TortoiseProc.exe";

        public static void CommitSvn(string path)
        {
            //转一遍路径是为了修复提交乱码问题
            var fixPath = path.Replace("/", "\\");
            bool isSuccess = ExecuteCommand(ClientCommand, $"/command:commit /path:\"{fixPath}\"",
                out _,
                out string errorDesc);
            if (!isSuccess)
            {
                Debug.LogErrorFormat("SVN 提交失败,路径：{0},错误信息：{1}", fixPath, errorDesc);
            }
        }


        public static void UpdateSvn(string path)
        {
            var fixPath = path.Replace("/", "\\");
            bool isSuccess = ExecuteCommand(ClientCommand, $"/command:update /path:\"{fixPath}\"",
                out _,
                out string errorDesc);
            if (!isSuccess)
            {
                Debug.LogErrorFormat("SVN 更新失败,路径：{0},错误信息：{1}", fixPath, errorDesc);
            }
            else
            {
                Debug.LogFormat("SVN 更新成功：路径：{0},信息：{1}", fixPath, errorDesc);
            }
        }

        private static bool ExecuteCommand(string cmdExe, string cmdParam, out string outputDesc, out string errorDesc)
        {
            outputDesc = string.Empty;
            errorDesc = string.Empty;
            if (string.IsNullOrEmpty(cmdParam) || string.IsNullOrEmpty(cmdExe))
            {
                errorDesc = "cmdParam或cmdParam为空，请检查参数是否正确";
                return false;
            }

            ProcessStartInfo info = new ProcessStartInfo(cmdExe);
            info.Arguments = cmdParam;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            try
            {
                Process p = Process.Start(info);
                if (p != null)
                {
                    outputDesc = p.StandardOutput.ReadToEnd();
                    errorDesc = p.StandardError.ReadToEnd();
                    p.Close();
                }

                return string.IsNullOrEmpty(errorDesc);
            }
            catch (Exception e)
            {
                errorDesc = e.Message;
                Debug.Log(e.Message);
            }

            return false;
        }
    }
}