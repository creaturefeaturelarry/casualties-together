using Together;
using UnityEngine;

namespace Together.UI.TeammateList;

internal sealed class Slot {
    public ScavPlayer Player;

    public Body Body;

    public string Name;

    public Color NameColor;

    public int Species = -1;

    public Sprite Head;

    public Sprite HeadDetail;

    public string Mood;

    public string Yap;

    public string Blood;

    public string Mind;

    public string Satiety;

    public int Stamp;

    public float Danger;

    public int MoodleCount;

    public readonly Sprite[] MoodleBack;

    public readonly Sprite[] MoodleIcon;

    public bool InRange = true;

    public bool TargetUnchipped;

    public bool ChipReadable => !TeammateListHost.ViewerUnchipped && !TargetUnchipped;

    public Slot() {
        Name = "";
        NameColor = Color.white;
        Mood = "";
        Yap = "";
        Blood = "";
        Mind = "";
        Satiety = "";
        MoodleBack = new Sprite[10];
        MoodleIcon = new Sprite[10];
    }
}