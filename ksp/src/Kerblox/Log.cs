using UnityEngine;

namespace Kerblox
{
    /// <summary>All plugin logging goes through here so `grep '\[Kerblox\]' KSP.log` finds everything.</summary>
    internal static class Log
    {
        private const string Prefix = "[Kerblox] ";

        public static void Info(string msg) => Debug.Log(Prefix + msg);
        public static void Warn(string msg) => Debug.LogWarning(Prefix + msg);
        public static void Error(string msg) => Debug.LogError(Prefix + msg);
    }
}
