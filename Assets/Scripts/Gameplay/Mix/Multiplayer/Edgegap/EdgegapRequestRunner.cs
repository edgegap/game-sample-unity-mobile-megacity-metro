using UnityEngine;

namespace Unity.MegacityMetro.Edgegap
{
    /// <summary>
    /// Hidden object that runs the Edgegap SDK's web request coroutines for systems, which cannot run coroutines.
    /// </summary>
    public class EdgegapRequestRunner : MonoBehaviour
    {
        public static EdgegapRequestRunner Create(string name)
        {
            var host = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            return host.AddComponent<EdgegapRequestRunner>();
        }
    }
}
