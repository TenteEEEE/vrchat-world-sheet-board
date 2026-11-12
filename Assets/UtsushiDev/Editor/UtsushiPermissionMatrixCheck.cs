#if UNITY_EDITOR
using Utsushi;
using UnityEditor;
using UnityEngine;

namespace UtsushiDev
{
    public static class UtsushiPermissionMatrixCheck
    {
        [MenuItem("Tools/TenteEEEE/Utsushi (Dev)/Verify Permission Matrix")]
        public static void RunPermissionMatrix()
        {
            GameObject host = new GameObject("Utsushi Permission Matrix Check");
            try
            {
                UtsushiSource source = host.AddComponent<UtsushiSource>();
                int[] permissions = {0, 1, 2};
                for (int audience = 0; audience < 3; audience++)
                {
                    source.audience = (UtsushiAudience)audience;
                    for (int i = 0; i < permissions.Length; i++)
                    {
                        int permission = permissions[i];
                        bool expected = audience == 0 || permission == 0 || (audience == 1 && permission == 1);
                        bool actual = source._PermissionValueAllowed(permission);
                        if (actual != expected) throw new System.InvalidOperationException("Permission matrix mismatch: audience=" + audience + " permission=" + permission);
                    }
                }
                Debug.Log("[Utsushi] Runtime permission matrix passed: 9 audience/permission combinations.");
            }
            finally { Object.DestroyImmediate(host); }
        }
    }
}
#endif
