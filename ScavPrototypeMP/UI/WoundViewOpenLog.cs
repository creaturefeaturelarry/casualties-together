using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Together {
    // one dump when the health panel opens, so a stretched doll can be compared across machines
    static class WoundViewOpenLog {
        public static void Dump() {
            try {
                var sb = new StringBuilder(4096);
                sb.AppendLine("[WoundView] open");
                AppendDisplay(sb);
                AppendCamera(sb);
                WoundView view = WoundView.view;
                if (view == null) {
                    sb.AppendLine("view: null");
                    log.l(sb.ToString(), false);
                    return;
                }
                AppendCanvas(sb, view);
                AppendImages(sb, view);
                log.l(sb.ToString(), false);
            } catch (System.Exception e) {
                log.error("[WoundView] open log failed: " + e);
            }
        }

        static void AppendDisplay(StringBuilder sb) {
            Resolution res = Screen.currentResolution;
            sb.Append("screen: ").Append(Screen.width).Append('x').Append(Screen.height)
                .Append(" dpi=").Append(Screen.dpi.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" fullscreen=").Append(Screen.fullScreen)
                .Append(" mode=").Append(Screen.fullScreenMode)
                .Append(" currentRes=").Append(res.width).Append('x').Append(res.height)
                .Append(" @").Append(res.refreshRateRatio.value.ToString("0.##", CultureInfo.InvariantCulture)).AppendLine("Hz");
            Display display = Display.main;
            if (display != null) {
                sb.Append("display: system=").Append(display.systemWidth).Append('x').Append(display.systemHeight)
                    .Append(" rendering=").Append(display.renderingWidth).Append('x').Append(display.renderingHeight)
                    .Append(" displays=").Append(Display.displays.Length).AppendLine();
                float screenAspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 0f;
                float panelAspect = display.systemHeight > 0 ? (float)display.systemWidth / display.systemHeight : 0f;
                sb.Append("aspect: backbuffer=").Append(screenAspect.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(" panel=").Append(panelAspect.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
            }
            try {
                DisplayInfo window = Screen.mainWindowDisplayInfo;
                RectInt work = window.workArea;
                sb.Append("monitor: \"").Append(window.name).Append("\" ")
                    .Append(window.width).Append('x').Append(window.height)
                    .Append(" work=").Append(work.width).Append('x').Append(work.height)
                    .Append(" at ").Append(work.x).Append(',').Append(work.y).AppendLine();
            } catch (System.Exception e) {
                sb.Append("monitor: unavailable (").Append(e.GetType().Name).AppendLine(")");
            }
        }

        static void AppendCamera(StringBuilder sb) {
            Camera cam = PlayerCamera.main != null ? PlayerCamera.main.GetComponent<Camera>() : null;
            if (cam == null && Camera.main != null) cam = Camera.main;
            if (cam == null) {
                sb.AppendLine("camera: null");
                return;
            }
            Rect pixel = cam.pixelRect;
            sb.Append("camera: ortho=").Append(cam.orthographic)
                .Append(" size=").Append(cam.orthographicSize.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" aspect=").Append(cam.aspect.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" pixel=").Append(cam.pixelWidth).Append('x').Append(cam.pixelHeight)
                .Append(" pixelRect=").Append(pixel.width.ToString("0.#", CultureInfo.InvariantCulture))
                .Append('x').Append(pixel.height.ToString("0.#", CultureInfo.InvariantCulture))
                .Append(" @").Append(pixel.x.ToString("0.#", CultureInfo.InvariantCulture))
                .Append(',').Append(pixel.y.ToString("0.#", CultureInfo.InvariantCulture))
                .Append(" rect=").Append(cam.rect).AppendLine();
        }

        static void AppendCanvas(StringBuilder sb, WoundView view) {
            Image sample = FirstImage(view);
            Canvas canvas = sample != null ? sample.canvas : null;
            if (canvas == null) {
                sb.AppendLine("canvas: null");
                return;
            }
            sb.Append("canvas: ").Append(canvas.name)
                .Append(" mode=").Append(canvas.renderMode)
                .Append(" scale=").Append(canvas.scaleFactor.ToString("0.####", CultureInfo.InvariantCulture))
                .Append(" pixelPerfect=").Append(canvas.pixelPerfect).AppendLine();
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) {
                sb.AppendLine("scaler: none");
            } else {
                sb.Append("scaler: mode=").Append(scaler.uiScaleMode)
                    .Append(" ref=").Append(scaler.referenceResolution.x.ToString("0.#", CultureInfo.InvariantCulture))
                    .Append('x').Append(scaler.referenceResolution.y.ToString("0.#", CultureInfo.InvariantCulture))
                    .Append(" match=").Append(scaler.screenMatchMode)
                    .Append(" widthOrHeight=").Append(scaler.matchWidthOrHeight.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(" refPPU=").Append(scaler.referencePixelsPerUnit.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(" scaleFactor=").Append(scaler.scaleFactor.ToString("0.####", CultureInfo.InvariantCulture))
                    .AppendLine();
            }
            RectTransform root = canvas.GetComponent<RectTransform>();
            if (root != null) {
                sb.Append("canvasRect: ");
                AppendRect(sb, root);
                sb.AppendLine();
            }
        }

        static void AppendImages(StringBuilder sb, WoundView view) {
            Image[] images = view.limbImages;
            if (images == null) {
                sb.AppendLine("limbs: null");
                return;
            }
            sb.Append("limbs: ").Append(images.Length).AppendLine();
            for (int i = 0; i < images.Length; i++) {
                AppendImage(sb, i.ToString(CultureInfo.InvariantCulture), images[i]);
                if (images[i] != null && images[i].transform.childCount > 0) {
                    AppendImage(sb, i.ToString(CultureInfo.InvariantCulture) + ".inner", images[i].transform.GetChild(0).GetComponent<Image>());
                }
            }
        }

        static void AppendImage(StringBuilder sb, string label, Image image) {
            if (image == null) {
                sb.Append(label).AppendLine(": null");
                return;
            }
            RectTransform rt = image.rectTransform;
            Sprite sprite = image.sprite;
            sb.Append(label).Append(": ").Append(image.name)
                .Append(" active=").Append(image.gameObject.activeInHierarchy)
                .Append(" preserve=").Append(image.preserveAspect);
            if (sprite != null) {
                Rect rect = sprite.rect;
                Texture tex = sprite.texture;
                sb.Append(" sprite=").Append(sprite.name)
                    .Append(" rect=").Append(rect.width.ToString("0.#", CultureInfo.InvariantCulture))
                    .Append('x').Append(rect.height.ToString("0.#", CultureInfo.InvariantCulture))
                    .Append(" ppu=").Append(sprite.pixelsPerUnit.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(" pivot=").Append(sprite.pivot.x.ToString("0.#", CultureInfo.InvariantCulture))
                    .Append(',').Append(sprite.pivot.y.ToString("0.#", CultureInfo.InvariantCulture));
                if (tex != null) {
                    sb.Append(" tex=").Append(tex.width).Append('x').Append(tex.height);
                }
            } else {
                sb.Append(" sprite=null");
            }
            sb.Append(" size=").Append(rt.sizeDelta.x.ToString("0.##", CultureInfo.InvariantCulture))
                .Append('x').Append(rt.sizeDelta.y.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" drawn=").Append(rt.rect.width.ToString("0.##", CultureInfo.InvariantCulture))
                .Append('x').Append(rt.rect.height.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" localScale=").Append(Fmt(rt.localScale))
                .Append(" lossy=").Append(Fmt(rt.lossyScale))
                .Append(" anchored=").Append(rt.anchoredPosition.x.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(',').Append(rt.anchoredPosition.y.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" anchorMin=").Append(Fmt(rt.anchorMin))
                .Append(" anchorMax=").Append(Fmt(rt.anchorMax))
                .Append(" pivot=").Append(Fmt(rt.pivot))
                .Append(" screen=").Append(rt.position.x.ToString("0.#", CultureInfo.InvariantCulture))
                .Append(',').Append(rt.position.y.ToString("0.#", CultureInfo.InvariantCulture));
            if (sprite != null && sprite.rect.width > 0.5f && sprite.rect.height > 0.5f) {
                sb.Append(" fit=").Append((rt.sizeDelta.x / sprite.rect.width).ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(',').Append((rt.sizeDelta.y / sprite.rect.height).ToString("0.###", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }

        static void AppendRect(StringBuilder sb, RectTransform rt) {
            sb.Append("rect=").Append(rt.rect.width.ToString("0.##", CultureInfo.InvariantCulture))
                .Append('x').Append(rt.rect.height.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" size=").Append(rt.sizeDelta.x.ToString("0.##", CultureInfo.InvariantCulture))
                .Append('x').Append(rt.sizeDelta.y.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(" localScale=").Append(Fmt(rt.localScale))
                .Append(" lossy=").Append(Fmt(rt.lossyScale))
                .Append(" anchorMin=").Append(Fmt(rt.anchorMin))
                .Append(" anchorMax=").Append(Fmt(rt.anchorMax));
        }

        static Image FirstImage(WoundView view) {
            Image[] images = view.limbImages;
            if (images == null) return null;
            for (int i = 0; i < images.Length; i++) {
                if (images[i] != null) return images[i];
            }
            return null;
        }

        static string Fmt(Vector2 v) {
            return v.x.ToString("0.###", CultureInfo.InvariantCulture) + "," + v.y.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string Fmt(Vector3 v) {
            return v.x.ToString("0.###", CultureInfo.InvariantCulture) + ","
                + v.y.ToString("0.###", CultureInfo.InvariantCulture) + ","
                + v.z.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
