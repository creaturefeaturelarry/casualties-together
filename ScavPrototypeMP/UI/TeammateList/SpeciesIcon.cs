using UnityEngine;

namespace Together.UI.TeammateList;

internal static class SpeciesIcon {
    private static WoundView _view;

    internal static void Assign(Slot slot, int charType) {
        if (slot == null) {
            return;
        }
        Resolve(charType, out Sprite head, out Sprite detail);
        slot.Head = head;
        slot.HeadDetail = detail;
    }

    private static void Resolve(int charType, out Sprite head, out Sprite detail) {
        head = null;
        detail = null;
        WoundView view = View();
        if (view == null || view.headCharSprites == null) {
            return;
        }
        int index = charType * 2;
        if (index < 0) {
            index = 0;
        }
        Sprite[] sprites = view.headCharSprites;
        if (index < sprites.Length) {
            head = sprites[index];
        }
        if (index + 1 < sprites.Length) {
            detail = sprites[index + 1];
        }
    }

    private static WoundView View() {
        if (_view != null) {
            return _view;
        }
        _view = WoundView.view;
        if (_view == null) {
            _view = Object.FindObjectOfType<WoundView>();
        }
        return _view;
    }
}