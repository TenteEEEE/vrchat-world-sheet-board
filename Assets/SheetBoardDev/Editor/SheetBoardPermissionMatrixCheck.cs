#if UNITY_EDITOR
using SheetBoard;
using UnityEditor;
using UnityEngine;

namespace SheetBoardDev
{
    public static class SheetBoardPermissionMatrixCheck
    {
        [MenuItem("Tools/TenteEEEE/Sheet Board (Dev)/Verify Permission Matrix")]
        public static void RunPermissionMatrix()
        {
            GameObject host = new GameObject("SheetBoard Permission Matrix Check");
            try
            {
                SheetBoardSource source = host.AddComponent<SheetBoardSource>();
                int[] permissions = {0, 1, 2};
                for (int audience = 0; audience < 3; audience++)
                {
                    source.audience = (SheetBoardAudience)audience;
                    for (int i = 0; i < permissions.Length; i++)
                    {
                        int permission = permissions[i];
                        bool expected = audience == 0 || permission == 0 || (audience == 1 && permission == 1);
                        bool actual = source._PermissionValueAllowed(permission);
                        if (actual != expected) throw new System.InvalidOperationException("Permission matrix mismatch: audience=" + audience + " permission=" + permission);
                    }
                }
                Debug.Log("[SheetBoard] Runtime permission matrix passed: 9 audience/permission combinations.");
            }
            finally { Object.DestroyImmediate(host); }
        }
    }
}
#endif
