#if UNITY_EDITOR
using System;
using SheetBoardEditor;
using UnityEditor;
using UnityEngine;

namespace SheetBoardDev
{
    public static class SheetBoardBatchBuild
    {
        private static bool sheetBoardCompileError;

        public static void RebuildAndPreview()
        {
            SheetBoardDevMenu.RunBatch(RebuildAndPreviewCore);
        }

        private static void RebuildAndPreviewCore()
        {
            try
            {
                sheetBoardCompileError = false;
                Application.logMessageReceived += CaptureCompileError;
                try { SheetBoardInstaller.BuildPrefabs(); }
                finally { Application.logMessageReceived -= CaptureCompileError; }
                if (sheetBoardCompileError) throw new InvalidOperationException("SheetBoard UdonSharp compile error; refusing to treat previous program assets as current.");
                SheetBoardPermissionMatrixCheck.RunPermissionMatrix();
                SheetBoardPreview.RenderSample();
            }
            finally { Application.logMessageReceived -= CaptureCompileError; }
        }

        private static void CaptureCompileError(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Error && message.Contains(SheetBoardInstaller.RootDir + "Runtime/") && message.Contains("UdonSharp")) sheetBoardCompileError = true;
        }
    }
}
#endif
