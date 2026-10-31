using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Device;
using UnityEngine.UI;

namespace Multiupdater
{
    internal class AutoupdaterGUI : MonoBehaviour {
        readonly int screenHeight = UnityEngine.Screen.height;
        readonly int bottomOffset = 100;
        public static string Status { get; set; }
        public static bool ShowUpdateButton { get; set; }
        public async void OnGUI() {
            GUI.Label(new Rect(5, screenHeight - bottomOffset + 25, 1000, 80), new GUIContent("MP Updater: " + Status));
            if (ShowUpdateButton) {
                if (GUI.Button(new Rect(5, screenHeight - bottomOffset, 60, 25), new GUIContent("Update MP"))) {
                    if (AutoUpdater.latestVersionAndURL.Item2 != null) {
                        await AutoUpdater.StartChecking();
                    }
                }
            }
            if (GUI.Button(new Rect(5 + 100, screenHeight - bottomOffset, 60, 25), new GUIContent("Close"))) {
                GameObject.Destroy(this);
                AutoupdaterGUI.ShowUpdateButton = false;
            }
        }
    }
}
