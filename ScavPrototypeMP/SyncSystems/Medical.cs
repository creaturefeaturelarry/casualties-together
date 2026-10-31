using HarmonyLib;
using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using static WoundView;

namespace Together;




public struct CharacterLimbHealthState : INetSerializeByMemcpy {
    Bitset8 pb1;


    public bool dismembered {
        get => pb1[0];
        set => pb1[0] = value;
    }
    // 1
    public bool infected {
        get => pb1[2];
        set => pb1[2] = value;
    }

    public bool has_splint {
        get => pb1[3];
        set => pb1[3] = value;
    }
    public bool splint_type {
        get => pb1[4];
        set => pb1[4] = value;
    }
    //private byte compressednum10;
    //public float splint_condition { get { return compressednum10 / 255f; } set { compressednum10 = (byte)(Mathf.Clamp(value, 0f, 1f) * 255f); } }

    public bool has_tourniquet {
        get => pb1[5];
        set => pb1[5] = value;
    }


    private byte shrapnel;

    private byte compressednumfurBloodAmount;
    public float furBloodAmount { get { return compressednumfurBloodAmount / 25.5f; } set { compressednumfurBloodAmount = (byte)(Mathf.Clamp(value, 0f, 10f) * 25.5f); } }

    private byte compressednum6;
    public float muscleHealth { get { return compressednum6 / 2.55f; } set { compressednum6 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }

    private byte compressednum7;
    public float skinHealth { get { return compressednum7 / 2.55f; } set { compressednum7 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }


    public float boneHealTimer;
    public float dislocationTimer;

    private byte compressednum9;
    public float infectionAmount { get { return compressednum9 / 2.55f; } set { compressednum9 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }

    private byte compressednum4;
    public float bleedAmount { get { return compressednum4 / 2.55f; } set { compressednum4 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }

    private byte compressednum5;
    public float pain { get { return compressednum5 / 2.55f; } set { compressednum5 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }


    public ushort disinfectionTime;

    private byte bandageSlowAmount;
    

    public CharacterLimbHealthState(Limb limb) {
        dismembered = limb.dismembered;
        shrapnel = (byte)limb.shrapnel; 
        infected = limb.infected;
        muscleHealth = limb.muscleHealth;
        skinHealth = limb.skinHealth;
        furBloodAmount = limb.furBloodAmount;
        if (!limb.broken) {
            boneHealTimer = 0;
        } else {
            boneHealTimer = limb.boneHealTimer;
        }
        if (!limb.dislocated) {
            dislocationTimer = 0;
        } else {
            dislocationTimer = limb.dislocationTimer;
        }
        infectionAmount = limb.infectionAmount;
        bleedAmount = limb.bleedAmount;
        pain = limb.pain;
        disinfectionTime = (ushort)Mathf.Clamp(limb.disinfectionTime,0,ushort.MaxValue);

        // uhh idk how much that matters 
        if (limb.bandageSlowAmount < 0.5f) {
            bandageSlowAmount = 0;
        } else {
            bandageSlowAmount = (byte)Mathf.Clamp(Mathf.Ceil(limb.bandageSlowAmount), 0, byte.MaxValue);
        }

        has_splint = limb.splinted;
        if (limb.TryGetComponent<SplintLimb>(out var splint)) {
            splint_type = splint.item == "carcasssplint";

            //splint_condition = splint.condition;
        } 

        if (limb.TryGetComponent(out TourniquetScript tourniquet)) {
            has_tourniquet = true;
        } else { 
            has_tourniquet = false; 
        }


    }

    // the slot on this limb, or on a hand still parented under it. no move: the host drop already published the ground byte.
    static void DropHeld(Limb limb) {
        if (limb == null || limb.body == null)
            return;
        InventorySlot slot = limb.GetComponentInChildren<InventorySlot>(true);
        if (slot == null || slot.body == null)
            return;
        MoveCommit.PushApply();
        try {
            slot.body.DropItem(slot.slot);
        } finally {
            MoveCommit.PopApply();
        }
    }

    // boots are children of the foot, or a sprite copy pointing at them. stock drops every limb further from the heart in the same call.
    static void DropWorn(Limb limb) {
        if (limb == null || limb.body == null)
            return;
        List<Limb> parts = TourniquetScript.GetLowerLimbs(limb);
        MoveCommit.PushApply();
        try {
            for (int i = 0; i < parts.Count; i++) {
                Limb part = parts[i];
                if (part == null)
                    continue;
                List<Item> worn = part.GetLimbWearables();
                for (int w = 0; w < worn.Count; w++) {
                    Item item = worn[w];
                    if (item == null)
                        continue;
                    limb.body.DropWearable(item);
                    // stock no-ops when GetWearable misses, and the boot sprites stay on the leg.
                    HeldSeat.Detach(item);
                }
            }
        } finally {
            MoveCommit.PopApply();
        }
    }

    public void Apply(Limb limb) {

        //Do sound
        if (dismembered && !limb.dismembered && SharedMain.LocalWorldIsGenerated) {
            limb.body.DoGoreSound();
            if (WoundView.view.body == limb.body) {
                WoundView.view.limbLookingAt = 1;
            }
            // the host already ran Dismember and dropped the held item and the boots.
            // this copy only learns the limb is gone, so the machete and the boot sprites stayed.
            DropHeld(limb);
            DropWorn(limb);
        }
        // a health snapshot is the final limb state. Limb.Dismember is an injury:
        // it damages connected limbs, drops items, and removes tourniquets.
        limb.dismembered = dismembered;
        limb.gameObject.SetActive(!dismembered);


        if (has_splint) {
            var splint = limb.GetComponent<SplintLimb>();

            if (splint == null) {
                splint = limb.AddComponent<SplintLimb>();
                if (splint_type) {
                    //OG: Item.SetupItems   carcasssplint
                    splint.conditionLossMinute = 0.036f;
                    splint.item = "carcasssplint";
                } else {
                    //OG: Item.SetupItems   splint
                    splint.conditionLossMinute = 0.015f;
                    splint.item = "splint";
                }
            }

            //splint.condition = splint_condition;
            splint.condition = 1.0f;

        } else if (limb.TryGetComponent<SplintLimb>(out var splint)) {
            UnityEngine.Object.Destroy(splint);
        }
        limb.splinted = has_splint;

        if (has_tourniquet) {
            var tourniquet = limb.GetOrAddComponent<TourniquetScript>();
        } else if (limb.TryGetComponent<TourniquetScript>(out var tourniquet)) {
            tourniquet.affectedLimbs.ForEach(delegate (Limb x)
            {
                x.blockedBleeding = false;
            });
            UnityEngine.Object.Destroy(tourniquet);
        }


        if (dislocationTimer > 0f) {
            if (!limb.dislocated) {
                limb.Dislocate();
            }
        } else {
            limb.dislocated = false;
        }
        if ((boneHealTimer > 0f)) {
            if (!limb.broken) {
                limb.BreakBone();
            }
        } else {
            limb.MendBone();
        }



        if (Net.IsClient && MinigameBase.main != null && MinigameBase.main.currentMinigame != null) {
            Minigame mg = MinigameBase.main.currentMinigame;
            if (mg is ShrapnelMinigame && limb.shrapnel != shrapnel)
                MinigameMPManager.NoteSlow("health shrapnel by " + limb.shrapnel + " -> " + shrapnel);
            if (mg is DislocationMinigame && Mathf.Abs(limb.dislocationTimer - dislocationTimer) > 8f)
                MinigameMPManager.NoteSlow("health dislocation by " + Mathf.RoundToInt(limb.dislocationTimer) + " -> " + Mathf.RoundToInt(dislocationTimer));
            if (mg is BandageMinigame && Mathf.Abs(limb.bandageSlowAmount - bandageSlowAmount) > 0.5f)
                MinigameMPManager.NoteSlow("health bandage by " + limb.bandageSlowAmount.ToString("0.0") + " -> " + bandageSlowAmount);
        }

        limb.furBloodAmount = furBloodAmount;
        limb.shrapnel = shrapnel; 
        limb.infected = infected;
        limb.muscleHealth = muscleHealth;
        limb.skinHealth = skinHealth;
        limb.boneHealTimer = boneHealTimer;
        limb.dislocationTimer = dislocationTimer;
        limb.infectionAmount = infectionAmount;
        limb.bleedAmount = bleedAmount;
        limb.pain = pain;
        limb.disinfectionTime = disinfectionTime;
        limb.bandageSlowAmount = bandageSlowAmount;
    }

    public static int GetByteSize() {
        return System.Runtime.InteropServices.Marshal.SizeOf(typeof(CharacterLimbHealthState));
    }
}

public struct CharacterHealthPainkillerStateSyncPacket : INetSerializeByMemcpy {

    public float opiateAmount;
    public float opiateTolerance;
    public float opiateReception;
    public float antagonistAmount;
    public float actualOpiateReception;


    public CharacterHealthPainkillerStateSyncPacket(Body body) {
        var painkiller = body.GetComponent<Painkillers>();

        opiateAmount = painkiller.opiateAmount;
        opiateTolerance = painkiller.opiateTolerance;
        opiateReception = painkiller.opiateReception;
        antagonistAmount = painkiller.antagonistAmount;
        actualOpiateReception = painkiller.actualOpiateReception;
    }

    public void Apply(Body body) {
        var painkiller = body.GetOrAddComponent<Painkillers>();
        painkiller.opiateAmount = opiateAmount;
        painkiller.opiateTolerance = opiateTolerance;
        painkiller.opiateReception = opiateReception;
        painkiller.antagonistAmount = antagonistAmount;
        painkiller.actualOpiateReception = actualOpiateReception;
    }
}

public struct CharacterSkillsStateSyncPacket : INetSerializeByMemcpy {

    public ushort skill_exp_STR;
    public ushort skill_exp_RES;
    public ushort skill_exp_INT;
    public ushort skill_STR;
    public ushort skill_RES;
    public ushort skill_INT;


    public CharacterSkillsStateSyncPacket(Body body) {
        var s = body.skills;

        // skills
        {
            skill_exp_STR = (ushort)s.expSTR;
            skill_exp_RES = (ushort)s.expRES;
            skill_exp_INT = (ushort)s.expINT;
        }
        //levels
        {
            skill_STR = (ushort)s.STR;
            skill_RES = (ushort)s.RES;
            skill_INT = (ushort)s.INT;
        }

    }
    public void Apply(Body body) {
        var s = body.skills;
        int wasStr = s.STR;
        int wasRes = s.RES;
        int wasInt = s.INT;
        // the first packet for this body is the join snapshot. a later rise is a real level.
        bool first = FirstApply(s);
        // levels first, then the stored exp, then the caps.
        // AddExp would add onto the exp already stored here and test the old cap,
        // so a full bar became one more level on the next fall.
        s.STR = skill_STR;
        s.RES = skill_RES;
        s.INT = skill_INT;
        s.expSTR = skill_exp_STR;
        s.expRES = skill_exp_RES;
        s.expINT = skill_exp_INT;
        s.UpdateExpBoundaries();
        if (first || !Util.IsBodyLocal(body))
            return;
        // the host hears this from AddExp. clients only learn the new level from this packet.
        Notice(wasStr, s.STR, "strength");
        Notice(wasRes, s.RES, "resilience");
        Notice(wasInt, s.INT, "intelligence");
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Skills, Skills> _applied = new System.Runtime.CompilerServices.ConditionalWeakTable<Skills, Skills>();

    static bool FirstApply(Skills skills) {
        if (skills == null || _applied.TryGetValue(skills, out _))
            return false;
        _applied.Add(skills, skills);
        return true;
    }

    static void Notice(int from, int to, string stat) {
        if (to <= from || PlayerCamera.main == null)
            return;
        PlayerCamera.main.DoAlert(Locale.GetOther("skillup").Replace("<>", Locale.GetOther(stat).ToLower()).Replace("<1>", from.ToString()).Replace("<2>", to.ToString()));
        Sound.Play("music/levelup", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
    }
}

public struct CharacterHealthStateSyncPacket : INetSerializeByMemcpy {
    ushort Tick;

    Bitset8 pb1;
    void SetDefaults() {
        this.usedNeuralBooster = false;
        this.maxSpeed = 25;
        this.moveForce = 4000;
        this.jumpSpeed = 26;
        this.caffeinated = 0;
        this.bloodVolume = 100;
        this.bloodOxygen = 100;
        this.bloodPressure = 120;
        this.respiratoryRate = 100;
        this.heartRate = 70;
        this.bloodVesselSize = 1;
        this.bloodViscosity = 0;
        this.strokeAmount = 0;
        this.hasPulmonaryEmbolism = false;
        this.fibrillationProgress = 0;
        this.venomCurrent = 0;
        this.venomTotal = 0;
        this.hunger = 109.5938f;
        this.thirst = 92.74957f;
        this.septicShock = 0;
        this.temperature = 37;
        this.sicknessAmount = 0;
        this.consciousness = 100;
        this.stamina = 100;
        this.happiness = 0;
        this.weightOffset = 0;
        this.radiationSickness = 0;
        this.internalBleeding = 0;
        this.hemothorax = 0;
        this.traumaAmount = 0;
        this.badSleepAmount = 0;
        this.hearingLoss = 0;
        this.antidepressantHappiness = 0;
        this.opiateHappiness = 0;
        this.antibioticImmunityTime = 0;
        this.brainGrowSickness = 0;
        this.lastStandTime = -10000;
        this.adrenaline = 0;
        this.stimulantMultiplier = 0;
    }

    public bool bothEyesGone {
        get => pb1[0];
        set => pb1[0] = value;
    }
    public bool disfigured {
        get => pb1[1];
        set => pb1[1] = value;
    }
    public bool eyeGone {
        get => pb1[2];
        set => pb1[2] = value;
    }
    public bool triedRollingLastStand {
        get => pb1[3];
        set => pb1[3] = value;
    }
    public bool succesfullyRolledLastStand {
        get => pb1[4];
        set => pb1[4] = value;
    }
    public bool mindwiped {
        get => pb1[5];
        set => pb1[5] = value;
    }
    public bool painkilled {
        get => pb1[6];
        set => pb1[6] = value;
    }
    public bool sleeping {
        get => pb1[7];
        set => pb1[7] = value;
    }


    CharacterSkillsStateSyncPacket skills;

    // health

    private byte compressedclawHealth;
    public float clawHealth { get { return compressedclawHealth / 2.55f; } set { compressedclawHealth = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }

    private byte compressednum3;
    public float brainHealth { get { return compressednum3 / 2.55f; } set { compressednum3 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }
    //public bool alive => brainHealth > 0;

    public bool usedNeuralBooster;
	public float maxSpeed;
	public float moveForce;
	public float jumpSpeed;
    public float caffeinated;

    public float bloodVolume ;
    public float bloodOxygen ;
    public float bloodPressure;
    public float respiratoryRate;
    
    public float heartRate ;
    public float bloodVesselSize ;
    public float bloodViscosity ;
    public float strokeAmount ;
    public bool hasPulmonaryEmbolism ;
    public float fibrillationProgress;

    public float venomCurrent;
    public float venomTotal;


    public float hunger;
    public float thirst;
    public float septicShock;
    public float temperature;
    public float sicknessAmount;
    public float consciousness;
    public float stamina;

    private byte compressednum4;
    public float energy { get { return compressednum4 / 2.55f; } set { compressednum4 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }

    public float happiness;
    public float weightOffset;// -80 to 100
    public float radiationSickness;
    public float internalBleeding;
    public float hemothorax;
    public float traumaAmount;

    private byte compressednum2;
    public float dirtyness { get { return compressednum2 / 2.55f; } set { compressednum2 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }

    private byte compressednum1;
    public float wetness { get { return compressednum1 / 2.55f; } set { compressednum1 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f); } }

    public float badSleepAmount;
    public float hearingLoss;
    public float antidepressantHappiness;
    public float opiateHappiness;
    public float antibioticImmunityTime;
    public float brainGrowSickness;
    public float lastStandTime;
    public float adrenaline;

    private byte compressednumb;
    public float burpTimer { get { return (compressednumb / 2.55f / 0.9f) - 100f; } set { compressednumb = (byte)(Mathf.Clamp((value + 100f) * 0.9f, 0, 100f) * 2.55f); } }

    public float stimulantMultiplier;

    // 15 limbs i think
    CharacterLimbHealthState limb0;
    CharacterLimbHealthState limb1;
    CharacterLimbHealthState limb2;
    CharacterLimbHealthState limb3;
    CharacterLimbHealthState limb4;
    CharacterLimbHealthState limb5;
    CharacterLimbHealthState limb6;
    CharacterLimbHealthState limb7;
    CharacterLimbHealthState limb8;
    CharacterLimbHealthState limb9;
    CharacterLimbHealthState limb10;
    CharacterLimbHealthState limb11;
    CharacterLimbHealthState limb12;
    CharacterLimbHealthState limb13;
    CharacterLimbHealthState limb14;


    public CharacterHealthStateSyncPacket() { }

    public void Write(Body body) {

        skills = new CharacterSkillsStateSyncPacket(body);

        // main body parameters
        {
            burpTimer = body.burpTimer;

            bothEyesGone = body.bothEyesGone;
            disfigured = body.disfigured;
            eyeGone = body.eyeGone;

            triedRollingLastStand = body.triedRollingLastStand;
            succesfullyRolledLastStand = body.succesfullyRolledLastStand;

            clawHealth = body.clawHealth;

            brainHealth = body.brainHealth;

            usedNeuralBooster = body.usedNeuralBooster;
            maxSpeed = body.maxSpeed;
            moveForce = body.moveForce;
            jumpSpeed = body.jumpSpeed;
            caffeinated = body.caffeinated;

            bloodVolume = body.bloodVolume;
            bloodOxygen = body.bloodOxygen;
            bloodPressure = body.bloodPressure;
            respiratoryRate = body.respiratoryRate;
            heartRate = body.heartRate;
            bloodVesselSize = body.bloodVesselSize;
            bloodViscosity = body.bloodViscosity;
            strokeAmount = body.strokeAmount;
            hasPulmonaryEmbolism = body.hasPulmonaryEmbolism;
            fibrillationProgress = body.fibrillationProgress;

            venomCurrent = body.venomCurrent;
            venomTotal = body.venomTotal;

            hunger = body.hunger;
            thirst = body.thirst;
            septicShock = body.septicShock;
            temperature = body.temperature;
            sicknessAmount = body.sicknessAmount;
            consciousness = body.consciousness;
            stamina = body.stamina;
            energy = body.energy;
            happiness = body.happiness;
            weightOffset = body.weightOffset;
            radiationSickness = body.radiationSickness;
            internalBleeding = body.internalBleeding;
            hemothorax = body.hemothorax;
            traumaAmount = body.traumaAmount;
            dirtyness = body.dirtyness;
            wetness = body.wetness;
            badSleepAmount = body.badSleepAmount;
            hearingLoss = body.hearingLoss;
            antidepressantHappiness = body.antidepressantHappiness;
            opiateHappiness = body.opiateHappiness;
            antibioticImmunityTime = body.antibioticImmunityTime;
            brainGrowSickness = body.brainGrowSickness;
            lastStandTime = body.lastStandTime;
            adrenaline = body.adrenaline;

            sleeping = body.sleeping;

            mindwiped = body.IsMindwiped();

            stimulantMultiplier = body.stimulantMultiplier;

            // because the component destroys itself if everything is back to normal
            if (body.TryGetComponent(out Painkillers component)) {
                painkilled = true;
            } else {
                painkilled = false;
            }
        }

        // limbs 
        {
            limb0 = new CharacterLimbHealthState(body.limbs[0]);
            limb1 = new CharacterLimbHealthState(body.limbs[1]);
            limb2 = new CharacterLimbHealthState(body.limbs[2]);
            limb3 = new CharacterLimbHealthState(body.limbs[3]);
            limb4 = new CharacterLimbHealthState(body.limbs[4]);
            limb5 = new CharacterLimbHealthState(body.limbs[5]);
            limb6 = new CharacterLimbHealthState(body.limbs[6]);
            limb7 = new CharacterLimbHealthState(body.limbs[7]);
            limb8 = new CharacterLimbHealthState(body.limbs[8]);
            limb9 = new CharacterLimbHealthState(body.limbs[9]);
            limb10 = new CharacterLimbHealthState(body.limbs[10]);
            limb11 = new CharacterLimbHealthState(body.limbs[11]);
            limb12 = new CharacterLimbHealthState(body.limbs[12]);
            limb13 = new CharacterLimbHealthState(body.limbs[13]);
            limb14 = new CharacterLimbHealthState(body.limbs[14]);
        }

        Tick = Net.ServerTick;
    }

    public void Apply(NetBody npc, bool force = false) {
        var seqDiff = LiteEntitySystem.Utils.SequenceDiff(Tick, npc._lastHealthPacketReceiveTick);
        if (
            !force &&
            seqDiff <= 0 
        ) {
#if TESTING 
            if (seqDiff < 0) {
                log.warn($"Health Packet too old: Diff: {seqDiff},  {npc}");
            }
#endif 
            return;
        }
        npc._lastHealthPacketReceiveTick = Tick;

        Apply(npc.body);
    }
    public void Apply(Body body) {

        var is_local = Util.IsBodyLocal(body);

        // regrow once when the snapshot's missing limbs do not match the rig.
        // agreeing packets must not rebuild the character every tick.
        if (body.IsAnyLimbMissing() && HasLimbStructureMismatch(body)) {
            Util.RegrowAllLimbs(body);
        }

        // limbs 
        {
            limb0.Apply(body.limbs[0]);
            limb1.Apply(body.limbs[1]);
            limb2.Apply(body.limbs[2]);
            limb3.Apply(body.limbs[3]);
            limb4.Apply(body.limbs[4]);
            limb5.Apply(body.limbs[5]);
            limb6.Apply(body.limbs[6]);
            limb7.Apply(body.limbs[7]);
            limb8.Apply(body.limbs[8]);
            limb9.Apply(body.limbs[9]);
            limb10.Apply(body.limbs[10]);
            limb11.Apply(body.limbs[11]);
            limb12.Apply(body.limbs[12]);
            limb13.Apply(body.limbs[13]);
            limb14.Apply(body.limbs[14]);
        }

        // main body parameters 
        {
            body.clawHealth = clawHealth;

            body.bothEyesGone = bothEyesGone;
            body.disfigured = disfigured;
            body.eyeGone = eyeGone;
            body.brainHealth = brainHealth;

            body.usedNeuralBooster = usedNeuralBooster;
            body.maxSpeed = maxSpeed;
            body.moveForce = moveForce;
            body.jumpSpeed = jumpSpeed;
            body.caffeinated = caffeinated;

            body.bloodVolume = bloodVolume;
            body.bloodOxygen = bloodOxygen;
            body.bloodPressure = bloodPressure;
            body.respiratoryRate = respiratoryRate;
            body.heartRate = heartRate;
            body.bloodVesselSize = bloodVesselSize;
            body.bloodViscosity = bloodViscosity;
            body.strokeAmount = strokeAmount;
            body.hasPulmonaryEmbolism = hasPulmonaryEmbolism;
            body.fibrillationProgress = fibrillationProgress;

            body.venomCurrent = venomCurrent;
            body.venomTotal = venomTotal;


            body.hunger = hunger;
            body.thirst = thirst;
            body.septicShock = septicShock;
            body.temperature = temperature;
            body.sicknessAmount = sicknessAmount;
            body.consciousness = consciousness;
            body.stamina = stamina;
            body.energy = energy;
            body.happiness = happiness;

            body.weightOffset = weightOffset;
            body.radiationSickness = radiationSickness;
            body.internalBleeding = internalBleeding;
            body.hemothorax = hemothorax;
            body.traumaAmount = traumaAmount;
            body.dirtyness = dirtyness;
            body.wetness = wetness;
            body.badSleepAmount = badSleepAmount;
            body.hearingLoss = hearingLoss;
            body.antidepressantHappiness = antidepressantHappiness;
            body.opiateHappiness = opiateHappiness;
            body.antibioticImmunityTime = antibioticImmunityTime;
            body.brainGrowSickness = brainGrowSickness;
            body.lastStandTime = lastStandTime;
            body.adrenaline = adrenaline;

            body.sleeping = sleeping;

            if (is_local) {
                if (succesfullyRolledLastStand && !body.succesfullyRolledLastStand) {
                    Plugin.log.LogInfo($"{UIMainMenu.USERINPUT_NAME}, rolled Last stand succesfully. ");

                    //OG: Body.TryLastStand V5t2 
                    PlayerCamera.main.StartCoroutine(PlayerCamera.main.LastStandSequence());
                }
            }
            body.triedRollingLastStand = triedRollingLastStand;
            body.succesfullyRolledLastStand = succesfullyRolledLastStand;

            if (mindwiped && body.mindWipe == null) {
                body.mindWipe = body.GetOrAddComponent<MindwipeScript>();
                body.mindWipe.active = true;
            } else if (!mindwiped && body.mindWipe != null) {
                UnityEngine.Object.Destroy(body.mindWipe);
            }

            if (!painkilled && body.TryGetComponent<Painkillers>(out var component)) {
                // because Painkillers deletes itself when these reach 0
                component.opiateAmount = 0f;
                component.opiateTolerance = 0f;
            }

            body.stimulantMultiplier = stimulantMultiplier;

            body.burpTimer = burpTimer;
        }

        skills.Apply(body);

        if (body.TryGetNetBody( out var npc)) {
            npc.last_mood = happiness;
        }

    }

    private bool HasLimbStructureMismatch(Body body) {
        return
            body.limbs[0].dismembered != limb0.dismembered ||
            body.limbs[1].dismembered != limb1.dismembered ||
            body.limbs[2].dismembered != limb2.dismembered ||
            body.limbs[3].dismembered != limb3.dismembered ||
            body.limbs[4].dismembered != limb4.dismembered ||
            body.limbs[5].dismembered != limb5.dismembered ||
            body.limbs[6].dismembered != limb6.dismembered ||
            body.limbs[7].dismembered != limb7.dismembered ||
            body.limbs[8].dismembered != limb8.dismembered ||
            body.limbs[9].dismembered != limb9.dismembered ||
            body.limbs[10].dismembered != limb10.dismembered ||
            body.limbs[11].dismembered != limb11.dismembered ||
            body.limbs[12].dismembered != limb12.dismembered ||
            body.limbs[13].dismembered != limb13.dismembered ||
            body.limbs[14].dismembered != limb14.dismembered;
    }

    public override string ToString() {
        return $"HealthPacket( Tick: {Tick} )";
    }
}











[HarmonyPatch(typeof(WoundViewLimb), nameof(WoundViewLimb.OnPointerEnter))]
public static class WoundViewLimb_OnPointerEnter_MultiplayerPatch {
    static bool Prefix(WoundViewLimb __instance) {
        // bug report by uzelezz
        // replaced PlayerCamera.main.body with the body field from woundview, so it will work if i have amputated limb
        if (!__instance.woundview.body.limbs[__instance.limb].dismembered) {
            __instance.woundview.limbLookingAt = __instance.limb;
            __instance.woundview.limbImageFlash[__instance.limb] = 1f;
        }
        return false;
    }
}




[HarmonyPatch(typeof(WoundView), nameof(WoundView.LayerTimerDisplay))]
static class WoundView_HLayerTimerDisplay_MultiplayerPatch {
    static void Postfix(WoundView __instance) {
        if (!Net.IsRunning) return;

        if ((int)(Time.time * 0.5f) % 2 == 0) {
            //"XXXXXXXXXXXXXXXXX"
            //"ALL:01 L:01 V:01X"

            __instance.timeTextBar.text = $"ALL:{ScavPlayer.BodyToPlayerDict.Count:D2}";
            if (Multiplayer.rules.TrackPlayerDeaths) {
                __instance.timeTextBar.text += $" L:{ScavPlayer.AllLivingPlayers.Count:D2}";
            }
            if (Multiplayer.rules.PlayerScatterDisallowed && Multiplayer.rules.ScatterMinGroupSize > 0 && UIInGame.main != null) {
                __instance.timeTextBar.text += $" V:{UIInGame.main.antidispersion_cur_range_count:D2}";
            }

        }
    }
}


// fix infections being show on non infected bodies when viewing other players wounds 
[HarmonyPatch(typeof(WoundView), nameof(WoundView.UpdateView))]
public static class WoundView_UpdateView_MultiplayerPatch {
    internal static bool[] OG_showInfection = null;

    public static GameObject CPRButton => PlayerCamera_ToggleWoundView_MultiplayerPatch.CPRButton;

    static bool Prefix(WoundView __instance) {
        if (__instance.body == null) {
            __instance.body = Util.GetLocalBodyNullable();
            return false;
        }

        if (OG_showInfection == null) {
            OG_showInfection = PlayerCamera.main.showInfection;
        }

        if (!Net.IsRunning) return true;



        if (__instance.body.TryGetNetBody(out var npc) && !npc.IsBodyLocal()) {

            PlayerCamera.main.showInfection = npc.localoverride_showInfection;

            // just in case, double check
            for (int i = 0; i < PlayerCamera.main.showInfection.Length; i++)
                if (!__instance.body.limbs[i].infected)
                    PlayerCamera.main.showInfection[i] = false;
        }

        if (CPRButton != null) {
            CheckIfCPRButtonShouldBeClickable(__instance);
        }

        return true;
    }
    static void CheckIfCPRButtonShouldBeClickable(WoundView ww) {
        var cprimage = CPRButton.GetComponent<Image>();
        var tooltip = CPRButton.GetComponent<UITooltip>();

        if (CPRHandler.CheckIfBodyIsNotOkAndNeedCPR(ww.body)) {
            if (CPRHandler.IsCPRBeingPerformedOnThisBody_IsMeOrNobody(ww.body)) {
                cprimage.color = Color.white;
                if (ww.body.alive) {
                    tooltip.tipDesc = Lang.Get("wv_cprdesc");
                } else {
                    tooltip.tipDesc = Lang.Get("plr_dead");
                }
                return;
            } else {
                tooltip.tipDesc = Lang.Get("wv_cprdesc_occupied");
            }
        } else {
            tooltip.tipDesc = Lang.Get("wv_cprdesc_no");
        }
        cprimage.color = Color.gray;
    }



    static void Postfix(WoundView __instance) {
        if (__instance.body == null) return;

        if (OG_showInfection != null) {
            PlayerCamera.main.showInfection = OG_showInfection;
        }

        if (!__instance.body.IsBodyLocal()) {
            __instance.miseryText.gameObject.SetActive(false);
        }

        UpdateFriendlyHealers(__instance);
    }

    internal static HashSet<ScavPlayer> lastHealerList = new();
    internal static List<GameObject> healerLimbTagIcons = new();
    internal static GameObject healerListUIElement = new();
    internal static void ClearHealerUI() {
        foreach (var item in healerLimbTagIcons) {
            GameObject.Destroy(item);
        }
        healerLimbTagIcons.Clear();

        GameObject.Destroy(healerListUIElement);
        healerListUIElement = null;

        foreach (var item in playerCursorSprites) {
            if (item.Value != null) {
                GameObject.Destroy(item.Value.gameObject);
            }
        }
        playerCursorSprites.Clear();

        dragItemSprites.Clear();

    }
    static void UpdateFriendlyHealers(WoundView wv) {
        NetBody viewTarget = null;
        if (wv.checkTime == 0) {

            foreach (var item in healerLimbTagIcons) {
                GameObject.Destroy(item);
            }
            healerLimbTagIcons.Clear();


            if (wv.body.TryGetNetBody(out viewTarget)) {
                var healersList = System.Linq.Enumerable.ToHashSet(ScavPlayer.AllLivingPlayers.Where(x => x.WoundviewTargetNetBodyId == viewTarget.netId && (Multiplayer.rules.DeadTextchat || x.IsAlive())));

                // Create player list on bottom right
                if (
                    !healersList.SetEquals(lastHealerList)
                ) {
                    lastHealerList = healersList;
                    GeneratePlayerList(wv, viewTarget);
                }


                if (healersList.Count() > 0
#if !RELEASE
                    || true
#endif
                ) {

                    var happyFace = wv.moodIcons[1];
                    var headSiblingIndex = wv.limbImages[0].transform.parent.GetSiblingIndex() + 1;
                    var siblingIndex = headSiblingIndex + 1 + wv.iconObj.Count;

                    try {
                        for (int k = 0; k < viewTarget.body.limbs.Length; k++) {
                            var limb = viewTarget.body.limbs[k];

                            var healersOnThisLimb = healersList.Where(x => x.IsInMinigame && x.minigame_targetobject.TryGetLimb(out var l) && l == limb).ToList();
                            if (healersOnThisLimb.Count > 0) {
                                var offCenterPos = new Vector2(17, -17) + (Vector2)wv.limbImages[k].transform.position;

                                var imgLerp = Mathf.Max(0.2f, wv.limbImageLerp[k]);
                                {
                                    // Draw Individual icons
                                    var healerList = healersOnThisLimb.ToList();

                                    for (int m = 0; m < healerList.Count; m++) {
                                        var plr = healerList[m];

                                        //OG: WoundView.UpdateView V7.0.1
                                        float num3 = ((float)m * 50f - (float)(healerList.Count - 1) * 25f) * (imgLerp * 0.8f + 0.2f) * 0.5f;
                                        Vector2 vector = new Vector2(num3, num3 * 0.24f) + offCenterPos;

                                        var uiIcon = wv.GenerateIcon(vector, happyFace);
                                        {
                                            Sprite ico = plr.IconSmallSprite;
                                            if (ico != null) {
                                                uiIcon.sprite = ico;
                                            } else {
                                                uiIcon.sprite = CoopModAssets.happy;
                                                uiIcon.color = plr.playerColor;
                                            }
                                        }
                                        uiIcon.name = $"FriendHeal {limb.fullName} {plr}";
                                        uiIcon.rectTransform.SetSiblingIndex(siblingIndex);

                                        healerLimbTagIcons.Add(uiIcon.gameObject);
                                    }
                                }

                            }
                        }
                    } catch (Exception ex) {
                        log.error(ex.ToString());
                    }
                }

            } else {
                if (healerListUIElement != null) {
                    GameObject.Destroy(healerListUIElement);
                }
                lastHealerList.Clear();
            }
        }

        if (wv.body.TryGetNetBody(out viewTarget)) {
            UpdateDragItems(wv, viewTarget);
        }
    }

    static void UpdateDragItems(WoundView wv, NetBody viewTarget) {
        foreach (var pair in dragItemSprites) {
            if (pair.Value == null) return;

            Sprite dragItemSprite = null;
            Item item = null;

            var plr = pair.Key;
            if (plr != null) {
                if (plr.IsInMinigame && plr.minigame_targetobject.TryGetNetBody(out var minigameTargetNb) && minigameTargetNb == viewTarget) {
                    item = plr.minigame_currentItem;
                    if (item != null) {
                        dragItemSprite = item.GetSprite();
                    }
                } else if (dragItemSprite == null && ItemSync.TryGetItem(plr.DragItemId, out var dragSi, out item)) {
                    dragItemSprite = item.GetSprite();
                }
            }

            pair.Value.gameObject.SetActive(dragItemSprite != null);
            if (pair.Value.gameObject.activeSelf) {
                //pair.Value.preserveAspect = true;  // give @a combatpen
                pair.Value.sprite = dragItemSprite;
                pair.Value.SetNativeSize();
                pair.Value.rectTransform.sizeDelta = PlayerCamera.ImageSizeDelta(dragItemSprite.texture, dragItemSprite.pixelsPerUnit, 30);

                if (item != null) {
                    var tt = PlayerCamera.ItemHoverDescription(item);
                    var uitooltip = pair.Value.GetOrAddComponent<UITooltip>();
                    uitooltip.skipLocale = true;
                    uitooltip.tipName = tt.Item1;
                    uitooltip.tipDesc = tt.Item2;
                }
            }
        }

        if (cursorSprites == null) {
            cursorSprites = PlayerCamera.main.cursors.Select(x => Util.CreateSprite(x)).ToList();
        }

        var scaler = PlayerCamera.main.mainCanvas.GetComponent<CanvasScaler>();
        foreach (var pair in playerCursorSprites) {
            if (pair.Value == null) return;
            var plr = pair.Key;
            var dragImage = pair.Value;
            var cursorObj = dragImage.transform.GetChild(0).GetComponent<Image>();
            if (
                plr != null && !plr.IsInMinigame
            ) {
                cursorObj.gameObject.SetActive(true);

                if (dragItemSprites.TryGetValue(plr, out var dragItem) && dragItem != null && dragItem.gameObject.activeSelf && dragItem.sprite != null) {
                    dragImage.color = new Color(1, 1, 1, 0.8f);
                    dragImage.sprite = dragItem.sprite;
                    dragImage.rectTransform.sizeDelta = PlayerCamera.ImageSizeDelta(dragImage.sprite.texture, 5f, 128f);
                } else {
                    dragImage.color = new Color(0, 0, 0, 0);
                }

                var cursorId = 3;
                if (plr.DragItemId != 0) {
                    cursorId = 1 + 6;
                } else if (plr.ButtonAttack || plr.ButtonIteminteract) {
                    cursorId = 3 + 6;
                }

                cursorObj.sprite = cursorSprites[cursorId];
                cursorObj.rectTransform.offsetMin = Vector2.zero;
                cursorObj.SetNativeSize();
                cursorObj.rectTransform.sizeDelta *= 0.8f;

                var pos = plr.CursorScreenPos;
                var halfRes = scaler.referenceResolution * 0.495f;
                dragImage.rectTransform.anchoredPosition = (((pos) * 0.5f * scaler.referenceResolution.y)).Clamp(-halfRes, halfRes);

            } else {
                cursorObj.gameObject.SetActive(false);
                dragImage.color = new Color(0, 0, 0, 0);
            }
        }
    }
    static List<Sprite> cursorSprites;

    static Dictionary<ScavPlayer, Image> playerCursorSprites = new();
    static Dictionary<ScavPlayer, Image> dragItemSprites = new();
    static void GeneratePlayerList(WoundView wv, NetBody viewTarget) {

        foreach (var item in playerCursorSprites) {
            if (item.Value != null)
                GameObject.Destroy(item.Value.gameObject);
        }
        playerCursorSprites.Clear();

        if (healerListUIElement != null) {
            GameObject.Destroy(healerListUIElement);
        }
        dragItemSprites.Clear();

        var headSiblingIndex = wv.limbImages[0].transform.parent.GetSiblingIndex() + 1;

        var canPush = ScavPlayer.TryGetLocalNetBody(out var localNetBody) && localNetBody.body.conscious && Multiplayer.rules.AllowPush;

        var transparentWhite = new Color(1, 1, 1, 0.6f);

        int defHeight = 40;

        {
            UIManager.CreateBlock(wv, "healerListUIElement", out healerListUIElement, out var healerListUIElementRT, out var healerListUIElementImage);
            healerListUIElementRT.pivot = new Vector2(0, 1);
            healerListUIElementRT.offsetMax = new Vector2(0, 0);
            healerListUIElementRT.offsetMin = new Vector2(0, 0);
            healerListUIElementImage.color = transparentWhite;

            healerListUIElementRT.anchoredPosition = new Vector2(0.6f, 0.5f);
            healerListUIElementRT.anchorMin = new Vector2(0.6f, 0.5f);
            healerListUIElementRT.sizeDelta = new Vector2(600f, defHeight);
            healerListUIElement.transform.SetSiblingIndex(headSiblingIndex);
        }

        UIManager.CreateLabel(healerListUIElement.transform, "wv_viewers", null, null, out var plrListGO, out var wvViewersLabel, out var plrListRT);

        int i = 0;
        foreach (var plr in lastHealerList) {
#if !RELEASE && false
            for (int u = 0; u < 4; u++) 
#else
            if (!viewTarget.IsLocal || !plr.IsLocal)
#endif
            {
                UIManager.CreateBlock(healerListUIElement.transform, $"healerListUIElementPlayer_{plr.playerId}", out var go, out var healerListUIElementRT, out var healerListUIElementImage);
                healerListUIElementRT.pivot = new Vector2(0, 1);
                healerListUIElementRT.offsetMax = new Vector2(0, 0);
                healerListUIElementRT.offsetMin = new Vector2(0, 0);
                healerListUIElementImage.color = transparentWhite;

                healerListUIElementRT.anchorMax = new Vector2(1, -(i));
                healerListUIElementRT.anchorMin = new Vector2(0, -(i + 1));

                healerListUIElementRT.sizeDelta = new Vector2(0, 0);

                UIManager.CreateLabel(healerListUIElementRT, healerListUIElementRT.name, null, null, out var labelGo, out var plrText, out var plrRt);
                plrText.text = plr.playerName;
                plrText.color = plr.playerColor;
                plrText.richText = false;

                CharStatusVisuals.TryMakeNametagFancy(plrText, plr);
                CharStatusVisuals.FancyNametagUpdateColor(plrText, plr.playerColor);


                var icon = plr.IconMediumSprite
#if !RELEASE 
                //    ?? happyFace
                //    ?? KrokoshaCoopModAssets.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
#endif  
                ;

                if (icon != null) {
                    UIManager.CreateBlock(healerListUIElementRT, $"plrIcon", out var go1, out var healerListUIElementRT1, out var healerListUIElementImage1);
                    healerListUIElementImage1.sprite = icon;

                    healerListUIElementRT1.anchorMin = new Vector2(0.98f, 0.5f);
                    healerListUIElementRT1.anchorMax = healerListUIElementRT1.anchorMin;
                    healerListUIElementImage1.SetNativeSize();
                    healerListUIElementRT1.pivot = new Vector2(1, 0.5f);
                    healerListUIElementRT1.sizeDelta = new Vector2(healerListUIElementRT.rect.height, healerListUIElementRT.rect.height) * 0.8f;
                }

                {
                    UIManager.CreateBlock(healerListUIElementRT, $"dragIcon_{plr.playerId}", out var go1, out var healerListUIElementRT1, out var healerListUIElementImage1);

                    healerListUIElementImage1.sprite = null;

                    healerListUIElementRT1.anchorMin = new Vector2(0.02f, 0.5f);
                    healerListUIElementRT1.anchorMax = healerListUIElementRT1.anchorMin;
                    healerListUIElementImage1.SetNativeSize();
                    healerListUIElementRT1.pivot = new Vector2(0, 0.5f);
                    healerListUIElementRT1.sizeDelta = new Vector2(healerListUIElementRT.rect.height, healerListUIElementRT.rect.height) * 0.7f;

                    dragItemSprites[plr] = healerListUIElementImage1;
                }
                if (!plr.IsLocal) {

                    UIManager.CreateBlock(wv, $"dragItem_{plr.playerId}", out var go1, out var healerListUIElementRT1, out var healerListUIElementImage1);

                    healerListUIElementImage1.sprite = null;
                    healerListUIElementImage1.color = new Color(0, 0, 0, 0);

                    healerListUIElementImage1.raycastTarget = false;
                    healerListUIElementRT1.offsetMax = new Vector2(40, 40);
                    healerListUIElementRT1.offsetMin = -healerListUIElementRT1.offsetMax;
                    healerListUIElementRT1.anchorMin = Vector2.one * 0.5f;
                    healerListUIElementRT1.anchorMax = Vector2.one * 0.5f;
                    healerListUIElementRT1.pivot = Vector2.one * 0.5f;
                    healerListUIElementImage1.SetNativeSize();


                    {
                        UIManager.CreateBlock(healerListUIElementRT1, $"playerCursor", out var go2, out var healerListUIElementRT2, out var healerListUIElementImage2);

                        healerListUIElementImage2.sprite = null;
                        healerListUIElementImage2.color = plr.playerColor.ToColorWithAlpha(0.8f);

                        healerListUIElementImage2.raycastTarget = false;
                        healerListUIElementRT2.offsetMin = Vector2.zero;
                        healerListUIElementRT2.offsetMax = new Vector2(40, 40);
                        healerListUIElementRT2.anchorMin = new Vector2(0.5f, 0.25f);
                        healerListUIElementRT2.anchorMax = healerListUIElementRT2.anchorMin;
                        healerListUIElementRT2.pivot = new Vector2(0, 1);
                        healerListUIElementImage2.SetNativeSize();
                    }

                    playerCursorSprites[plr] = healerListUIElementImage1;
                }

                if (
                    viewTarget.IsLocal // I am looking at myself, I should be able to defend myself 
                    && canPush
                    && !plr.IsLocal
                ) {
                    UIManager.CreateButton(healerListUIElementRT, $"plrint_push", null, null, out var go1, out var text1, out var healerListUIElementRT1, out var button, out var healerListUIElementImage1);
                    healerListUIElementImage1.sprite = UIManager.uiBlockNano;
                    healerListUIElementImage1.color = transparentWhite;

                    healerListUIElementRT1.anchorMin = new Vector2(0.99f, 0.5f);
                    healerListUIElementRT1.anchorMax = healerListUIElementRT1.anchorMin;
                    healerListUIElementRT1.pivot = new Vector2(0f, 0.5f);
                    healerListUIElementRT1.sizeDelta = new Vector2(text1.preferredWidth, text1.preferredHeight) * 0.9f;
                    text1.margin *= 1.5f;

                    button.onClick.AddListener(() => {
                        localNetBody.Push(plr.netBody);
                    });
                }

                i++;
            }
        }
        if (i == 0) {
            GameObject.Destroy(healerListUIElement);
        }


    }

}





[HarmonyPatch(typeof(WoundView), nameof(WoundView.TakeANap))]
public static class WoundView_TakeANap_MultiplayerPatch {
    static void Prefix(WoundView __instance) {
        if (PlayerCamera.main.body.canTakeNap) {
            // og code will make the char sleep

            // so i need to tell server that im boutta eep

            var plr = ScavPlayer.LocalPlayer;
            if (plr != null && plr.TryGetNetBody(out var nb) ) {

                nb.SetNetHealthSyncIgnoreTime(2); // sleep animation takes at most 1.95 secs i think


                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerTakeANap, true);
            }

        }
    }



    [ServerReceiver((byte)NetmsgId.SERVER_PlayerTakeANap)]
    static void Server_PlayerTakeANap(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) && body.conscious) {
            if (!Util.IsBodyLocal(body)) {
                PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = true;
            }

            body.TakeANap();
            PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = false;

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(body.transform.position, $"S: PlayerTakeANap {plr} ");

            var writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerTakeANap_Relay);
            writer.Put(plr.netBody.netId);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
            // WRITER END;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerTakeANap_Relay)]
    static void Client_PlayerTakeANap_Relay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid who_netid);

        if (NetBody.TryGetNetBodyFromId(who_netid, out var nb)) {

            if (!nb.body.sleeping) {
                if (!Util.IsBodyLocal(nb.body)) {
                    PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = true;
                }
                nb.body.TakeANap();
                PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = false;
            }

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(nb.position, $"C: PlayerTakeANap_Relay {nb} ");

        }
    }


    [ServerReceiver((byte)NetmsgId.SERVER_PlayerWakeTheFuckUpSamurai)]
    static void Server_IWannaWakeUp(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) && body.alive) {
            body.WakeUp();

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(body.transform.position, $"S: Wake up {plr} ");
        }
    }

}










[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ToggleWoundView))]
public static class PlayerCamera_ToggleWoundView_MultiplayerPatch {
    public static bool IgnoreNext = false;

    public static GameObject CPRButton = null;


    public static void WoundView_SetTargetBody(Body b) {

        var wv = WoundView.view;
        // always force set it idk
        wv.body = b;

        //OG: PlayerCamera.ToggleWoundView V5t2
        const string defaultname = "EXPERIMENT"; // default

        var looking_at_someone_else = !Util.IsBodyLocal(wv.body);

        wv.napbutton.gameObject.SetActive(!looking_at_someone_else);

        var workoutbutton = wv.transform.Find("WorkoutButton");
        if (workoutbutton != null) {

            if (CPRButton == null) {
                try {
                    CPRButton = GameObject.Instantiate(workoutbutton.gameObject, workoutbutton.transform.parent, false);
                    CPRButton.SetActive(true);
                    CPRButton.name = "BUTTON_MP_CPR";

                    var rt = CPRButton.GetComponent<RectTransform>();
                    var tooltip = CPRButton.GetComponent<UITooltip>();

                    tooltip.skipLocale = true;
                    tooltip.tipName = Lang.Get("wv_cpr");
                    tooltip.tipDesc = Lang.Get("wv_cprdesc");

                    rt.SetSiblingIndex(workoutbutton.GetSiblingIndex());
                    var img = CPRButton.GetComponent<Image>();
                    img.sprite = CoopModAssets.cpr_button_icon;
                    var button = CPRButton.GetComponent<UnityEngine.UI.Button>();
                    button.onClick.RemoveAllListeners();
                    button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                    button.onClick.AddListener(() => {
                        if (CPRHandler.CheckIfBodyIsNotOkAndNeedCPR(WoundView.view.body) && CPRHandler.IsCPRBeingPerformedOnThisBody_IsMeOrNobody(WoundView.view.body) ) {
#if !RELEASE
                            log.l("OPENING CPR !!!");
#endif
                            if (MinigameBase.main.currentMinigame != null && MinigameBase.main.currentMinigame is ManualDefibMinigame) {
                                MinigameBase.main.EndMinigame();
                            }
                            MinigameBase.main.StartMinigame(new CPRMinigame(WoundView.view.body), null);
                        }
                    });

                }  catch (Exception e) {
                    log.error("CPRButton creation: " + e.ToString());
                }
            }


            workoutbutton.gameObject.SetActive(!looking_at_someone_else);
            workoutbutton.GetComponent<UnityEngine.UI.Button>().interactable = !looking_at_someone_else;
        } else { // thats a problem
            log.error($"Couldn't find the 'WorkoutButton' in 'WoundView' !!!!!!!! ");
        }

        if (CPRButton != null) {
            CPRButton.SetActive(looking_at_someone_else);
            CPRButton.GetComponent<UnityEngine.UI.Button>().interactable = looking_at_someone_else;
        } else { // thats a problem
            log.error($"Couldn't find the patched 'CPRButton' !!!!!!!! ");
        }


        if (looking_at_someone_else) {

            if (b.TryGetNetBody(out var nb)) {
                wv.nameText.text = nb.bodyName;

                if (nb.IsPlayer) {
                    wv.bodyStatText.text = Lang.Get("wv_peerid") + nb.plr.playerId;
                } else {
                    wv.bodyStatText.text = Lang.Get("wv_peerid") + nb.netId;
                }

            } else { // thats a problem
                Plugin.log.LogError($"Couldnt get NetBody of a body, what??? {wv.body} ");
                wv.nameText.text = defaultname;
                wv.bodyStatText.text = "ERROR";
            }

        } else { // set it back to myself
            wv.nameText.text = defaultname;
            wv.SetCharDetails(wv.cInfo[0], wv.cInfo[1], wv.cInfo[2], wv.cInfo[3]);
        }

        WoundView_AddImageToLimb_MultiplayerPatch.RefreshAllLimbImages();
    }

    public static bool IsOpen() {
        return WoundView.view.gameObject.activeSelf;
    }

    // the panel turns the inventory hud off. stock turns it back on only after a refresh, and that refresh throws once the body is gone.
    public static bool HealthTargetGone() {
        var view = WoundView.view;
        if (view == null) return true;
        var b = view.body;
        if (b == null) return true;
        if (!Net.IsRunning) return false;
        if (!b.TryGetNetBody(out var nb)) return true;
        return nb.syncBody == null;
    }

    public static void CloseHealthPanel(PlayerCamera cam) {
        body_to_open = null;
        if (cam == null) return;
        if (cam.woundView != null)
            cam.woundView.SetActive(false);
        if (cam.mainView != null)
            cam.mainView.SetActive(true);
        if (UIInGame.interaction_menu_target_body != null && PlayerCamera.main != null)
            UIInGame.StopPlayerInteractionMenu();
    }
    public static void OpenSpecificBody(Body b, bool sound = true) {
        if (IsOpen() && b == WoundView.view.body) {
            return;
        }
        body_to_open = b;
        PlayerCamera.main.ToggleWoundView(sound);
    }
    public static Body body_to_open = null;
    public static bool Prefix(PlayerCamera __instance, bool sound) {

        WoundView_UpdateView_MultiplayerPatch.lastHealerList.Clear();
        WoundView_UpdateView_MultiplayerPatch.ClearHealerUI();


        if (IgnoreNext) {
            IgnoreNext = false;
            return false;
        }

        // already open on a body that left, or whose world just unloaded. dont let stock refresh it.
        if (IsOpen() && HealthTargetGone()) {
            CloseHealthPanel(__instance);
            return false;
        }


        if (!IsOpen()) { // woundview will get activated now

            if (Util.IsLocalBodyAlive() && CoopKeybinds.IsWoundViewSameAsOG() && UnityEngine.Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.woundview))) {

                var b = Util.GetBodyOnPos(Util.GetCursorWorldPos());
                if (b != null && !b.IsBodyLocal()) {
                    if (KM.DistanceCheck(Util.GetLocalBody(), b, SharedMain.max_player_interaction_distance)) {
                        body_to_open = b;
                    }
                }
            }

            if (body_to_open == null && UIInGame.SpectatorModeActive && __instance.following != null && __instance.following.TryGetComponent<Body>(out var targetbody)) {
                WoundView_SetTargetBody(targetbody);
            } else {
                WoundView_SetTargetBody(body_to_open ?? PlayerCamera.main.body);
            }
        }
        body_to_open = null;



        // because it checks if local plr is alive 
        if (UIInGame.SpectatorModeActive && !__instance.body.alive) {
            //OG PlayerCamera.ToggleWoundView V5t4 
            var view = WoundView.view.gameObject;
            if (sound) {
                __instance.PlayUISound(view.activeSelf ? PlayerCamera.UISoundType.Close : PlayerCamera.UISoundType.HealthPanelOn);
            }
            view.SetActive(!view.activeSelf);
            WoundView.view.UpdateView();
            WoundView.view.timeOpen = 0f;
            __instance.mainView.SetActive(!__instance.mainView.activeSelf);
            return false;
        }
        return true;
    }
}

















[HarmonyPatch(typeof(SplintLimb), nameof(SplintLimb.Start))]
static class SplintLimb_Start_MultiplayerPatch {
    static void Postfix(SplintLimb __instance) {
        if (Net.IsRunning) {
            // add the heal multiplier 
            __instance.conditionLossMinute += __instance.conditionLossMinute * Multiplayer.rules.AdditionalHealthDecay;
        }
    }
}







[HarmonyPatch(typeof(WoundView), nameof(WoundView.AddImageToLimb))]
public static class WoundView_AddImageToLimb_MultiplayerPatch {

    class WoundviewLimbImagePerPlayerTrackerComponent : MonoBehaviour {
        public Limb limb;
        public Body body => limb.body;
        public NetBody pb => body.GetNetBody();
        public UnityEngine.UI.Image image => GetComponent<UnityEngine.UI.Image>();
        
        public void RefreshLimbImage() {
            image.enabled = (body == WoundView.view.body && !limb.dismembered);
        }
    }

    public static void RefreshAllLimbImages() { 
        var a = WoundView.view.GetComponentsInChildren<WoundviewLimbImagePerPlayerTrackerComponent>();
        foreach (var item in a) {
            item.RefreshLimbImage();
        }
    }

    static Body oldbody = null;
    static void Prefix(WoundView __instance, ref UnityEngine.UI.Image __result, Limb limb, Sprite sprite, bool flip, LimbCondition removeCondition) {
        oldbody = __instance.body;
        __instance.body = limb.body;
    }
    static void Postfix(WoundView __instance, ref UnityEngine.UI.Image __result, Limb limb, Sprite sprite, bool flip, LimbCondition removeCondition ) {
        try {
            var compo = __result.AddComponent<WoundviewLimbImagePerPlayerTrackerComponent>();
            compo.limb = limb;
            __instance.body = oldbody;
            compo.RefreshLimbImage();
        } finally {
            __instance.body = oldbody;
        }
    }
}

    



[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.WoundSpecialAction))]
public static class PlayerCamera_WoundSpecialAction_MultiplayerPatch {



    public static void OG_TakeOffReimplemented(Body healer, TourniquetScript tourniquet) {
        //OG: TourniquetScript.TakeOff  THE ENTIRE FUNCTION I REIMPLEMENTED IT HERE
        var limb = tourniquet.GetComponent<Limb>();
        if (!Multiplayer.IsNetworkActiveAndIsClient()) {
            var gameObject = Utils.Create(("tourniquet"), tourniquet.transform.position, 0);
            gameObject.GetComponent<Item>().condition = tourniquet.condition;
            healer.AutoPickUpItem(gameObject.GetComponent<Item>());
        } else if (healer.IsBodyLocal()) {
            if (limb.TryGetNetBody(out var pacient))
                pacient.SetNetHealthSyncIgnoreTime(0.05f);
        }
        tourniquet.affectedLimbs.ForEach(delegate (Limb x)
        {
            x.blockedBleeding = false;
        });
        UnityEngine.Object.Destroy(tourniquet);
    }
    public static void OG_TakeOffReimplemented(Body healer, SplintLimb splint) {
        //OG: SplintLimb.TakeOff  THE ENTIRE FUNCTION I REIMPLEMENTED IT HERE
        var limb = splint.GetComponent<Limb>();
        limb.splinted = false;
        if (!Multiplayer.IsNetworkActiveAndIsClient()) {
            var gameObject = Utils.Create((splint.item), splint.transform.position, 0);
            gameObject.GetComponent<Item>().condition = splint.condition;
            healer.AutoPickUpItem(gameObject.GetComponent<Item>());
        } else if (healer.IsBodyLocal()) {
            if (limb.TryGetNetBody(out var pacient)) 
                pacient.SetNetHealthSyncIgnoreTime(0.05f);
        }
        UnityEngine.Object.Destroy(splint);
    }


    public static void OG_WoundSpecialAction(Body healer, Limb targetLimb) {
       
        var healerIsLocal = Util.IsBodyLocal(healer);

        //OG: PlayerCamera.WoundSpecialAction  THE ENTIRE FUNCTION I REIMPLEMENTED IT HERE
        if (healer.conscious) {
            if (targetLimb.TryGetComponent(out TourniquetScript component)) {
                OG_TakeOffReimplemented(healer, component);
            } else if (targetLimb.hasShrapnel && healerIsLocal) {
                MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = false;
                MinigameBase.main.StartMinigame(new ShrapnelMinigame(targetLimb), null);
            }
            if (targetLimb.TryGetComponent(out SplintLimb component2)) {
                OG_TakeOffReimplemented(healer, component2);
            } else if (targetLimb.dislocated && healerIsLocal) {
                MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = false;
                MinigameBase.main.StartMinigame(new DislocationMinigame(targetLimb), null);
            }
        }

        if (targetLimb.TryGetNetBody(out var nbPacient) && healerIsLocal) {
            nbPacient.SetNetHealthSyncIgnoreTime(0.01f);
        }
            
    }




    public static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning) {
            return true; // do default if singleplayer
        }

        if (__instance.body.allowUseItem) {

            if (!__instance.body.conscious) {
                return false;
            }

            if (!Util.DoFullInteractionCheck(__instance.body, __instance.selectedLimb.body, true)) {
                PlayerCamera.main.DoAlert(Lang.Get("plr_unreachable"));  
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
                return false;
            }

            if (__instance.body.allowUseItem) {
                if (__instance.selectedLimb.body.TryGetNetBody(out var pb)) {

                    if (log.verbose) log.l($"CLIENT: Sending WoundSpecialAction");


                    // send WoundSpecialAction announcement to the server

                    var writer = Net.CreateWriter(NetmsgId.SERVER_PlayerCamera_WoundSpecialAction);
                    writer.Put(new LimbNetId(__instance.selectedLimb));
                    Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    // WRITER END;


                    if (Net.IsClient) {
                        pb.SetNetHealthSyncIgnoreTime(0.05f);
                    }
                }
            } else {
                PlayerCamera.main.UseFailUnhappiness();
                return false;
            }

            OG_WoundSpecialAction(__instance.body, __instance.selectedLimb);


        } else {
            PlayerCamera.main.UseFailUnhappiness();
        }

        // i completely override the function
        return false;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ApplyWoundItem))]
public static class PlayerCamera_ApplyWoundItem_MultiplayerPatch {

    public static Body last_wounditem_user;



    public static bool DoApplyWoundItemChecks(Body healer, Limb limb, Item item) {

        var healer_is_me = Util.IsBodyLocal(healer);
        var otherbody = limb.body;

        //OG: PlayerCamera.ApplyWoundItem V5t2
        if (limb.dismembered || !healer.conscious || !item.Stats.ActuallyUsableOnLimb(item)) {
            return false;
        }



        if (otherbody != healer) {
            var pc = PlayerCamera.main;

            if (!otherbody.alive) { // nah, allow it, for confused healing of an already dead friend or cannibalism amputation
                //if (healer_is_me) { 
                //    pc.DoAlert(Lang.Get("deny_heal_dead"));  
                //    pc.PlayUISound(PlayerCamera.UISoundType.Deny);
                //}
                //return false;
                return true;
            } else if (healer.aboveMedicalCutoff) {

                // nah
                //if (!Util.DoFullInteractionCheck(healer, otherbody, healer_is_me, healer_is_me ? ClientMain.max_player_interaction_distance : ClientMain.max_player_interaction_distance * 1.4f)) {
                //    if (healer_is_me) {
                //        pc.DoAlert(Lang.Get("plr_unreachable"));  
                //        pc.PlayUISound(PlayerCamera.UISoundType.Deny);
                //    }
                //    return false;
                //}

                if (MedicalSync.IsRefusingHelp(otherbody) && !item.Stats.ignoreDepression) {
                    if (healer_is_me) {
                        pc.DoAlert(Lang.Get("deny_refuse"));  
                        pc.PlayUISound(PlayerCamera.UISoundType.Deny);
                    }
                } else {
                    return true;
                }
            } else {
                if (healer_is_me) {
                    pc.UseFailUnhappiness();
                }
                return false;
            }
            return false;
        } else {
            //OG: PlayerCamera.ApplyWoundItem V5t2
            if (!healer.aboveMedicalCutoff && !item.Stats.ignoreDepression) {
                if (healer_is_me) {
                    PlayerCamera.main.DoAlert(Locale.GetOther("alerttoounhappy"));
                }
            } else {
                return true;
            }
            return false;
        }
    }

    public static void ForceApplyWoundItem(Body healer ,Limb limb, Item item) {
        PlayerCamera_ApplyWoundItem_MultiplayerPatch.last_wounditem_user = healer;


        //OG: PlayerCamera.ApplyWoundItem V5t2

        limb.GetCoUtilsInstance().SelectForMPOverride();
        WaterContainerItem component;
        if (item.Stats.usableOnLimb) {
            item.Stats.useLimbAction(limb, item);
        } else if (item.TryGetComponent(out component)) {
            component.ApplyToLimb(limb);
        }
    }

    public static Limb applying_on_limb = null;
    public static Item applying_item = null;

    static bool Prefix(PlayerCamera __instance, Item item) {
        applying_on_limb = __instance.selectedLimb;
        applying_item = item;
        if (Net.IsRunning) {

            if (!ItemSync.CheckIfBodyReachThisItem(item, __instance.body, 20f, true)) {
                PlayerCamera.main.DoAlert(Lang.Get("item_unreachable"));  
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
                return false;
            }

            if (DoApplyWoundItemChecks(__instance.body, __instance.selectedLimb, item)) {
                if (ItemSync.TryGetItemSyncInfo(item, out var isi)) {


                    if (log.verbose) log.l($"CLIENT: Sending ApplyWoundItem {__instance.selectedLimb.body.GetNetBody()} {Util.GetLimbIndex(__instance.selectedLimb)} {isi} ");
                    


                    if (Net.IsClient) {
                        isi.SetIgnoreTimeForRoundTrip();
                    }


                    // request useLimbAction

                    var writer = Net.CreateWriter(NetmsgId.SERVER_PlayerCamera_ApplyWoundItem);

                    writer.Put(new LimbNetId(__instance.selectedLimb));
                    writer.Put(isi.syncId);

                    Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                    // WRITER END;


                    if (Net.IsServer) {
                        // server triggers the useLimbAction inside the receiver of this
                        return false;
                    }

                }
            } else {
                // cuz i basically reimplemented the entire function
                return false;
            }

        }
        // only network client will get here, it will call the useLimbAction
        last_wounditem_user = Util.GetLocalBody();
        return true;
    }

    static void Postfix(PlayerCamera __instance) {
        applying_on_limb = null;
        applying_item = null;
    }
}






[HarmonyPatch(typeof(Limb), nameof(Limb.FurBloodUpdate))]
public static class Limb_FurBloodUpdate_MultiplayerPatch {
    static void Postfix(Limb __instance) {
        if (__instance.body.bloodVolume <= 0 || __instance.body.totalBleedSpeed <= 0) {
            var mat = __instance.GetComponent<SpriteRenderer>().sharedMaterial;

            foreach (var item in __instance.GetComponentsInChildren<ParticleSystem>()) {
                if (item.name.Contains("BleedParticle")) { // bleeding particle
                    var emission = item.emission;
                    emission.rateOverTime = 0;
                }
            }
        }
    }
}


[HarmonyPatch(typeof(Limb), nameof(Limb.Dislocate))]
public static class Limb_Dislocate_MultiplayerPatch {

    static void Prefix(Limb __instance) {
        if (!__instance.dislocated && __instance.body.TryGetNetBody( out var pb)) {
            //pb.CameraShakeOverrideFunc(50f);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = instructions;
        
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }
}


[HarmonyPatch(typeof(Limb), nameof(Limb.BreakBone))]
public static class Limb_BreakBone_MultiplayerPatch {

    static bool Prefix(Limb __instance) {
        if (__instance.dismembered) {
            // this is to prevent error that happens when Head is dismembered and a random number attempts to disfigure face in BreakBone 
            return false;
        }
        return true;
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        var codes = instructions;

        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }
}



// cavetick swarms may cause broken bones
[HarmonyPatch(typeof(Limb), nameof(Limb.OnCollisionEnter2D))]
public static class Limb_OnCollisionEnter2D_MultiplayerPatch {
    public static Collision2D CurrentCollision = null;
    static bool Prefix(Limb __instance, Collision2D col) {
        if (!Net.IsRunning) return true;
        if (col.rigidbody != null && col.rigidbody.TryGetComponent(out SpiderHandler spider)) {
            return false;
        }
        CurrentCollision = col;

        if (__instance.IsBodyLocal()) {
            var go = col.gameObject;
            if (go != null && go.TryGetTracker(out var tracker)) {
                if (tracker.syncInfo.IsContactDangerous()) {
                    tracker.syncInfo.relaxed_combat_sync = true;
                    //tracker.syncInfo.SetIgnoreTimeForRoundTrip(0);
                }
            }
        }
        return true;
    }
    static void Postfix(Limb __instance) {
        CurrentCollision = null;
    }
}



[HarmonyPatch(typeof(Limb), nameof(Limb.ImpactDamage))]
public static class Limb_ImpactDamage_MultiplayerPatch {

    // ITS ACTUALLY ALREADY OVERRIDEN WITH THE SPOOFED fallShakeCooldown
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        var codes = instructions;

        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);

        return codes;
    }

    public static void Force(Limb __instance, float force, Vector2 dir) {
        force_run = true;
        __instance.ImpactDamage(force, dir);
        force_run = false;
    }
    static bool force_run = false;

    public static bool last_impact_is_local_body = false;

    static bool Prefix(Limb __instance, float force, Vector2 dir) {
        last_impact_is_local_body = __instance.IsBodyLocal();
        var body = __instance.body;

        if (!last_impact_is_local_body) body.fallShakeCooldown = 1f;
        if (SharedMain.LocalWorldIsGenerating) return false; 
        if (!Net.IsRunning) return true;

        if (!force_run) {


            if (__instance.TryGetNetBody(out var netBody)) { 
                if (netBody.piggybacking_on != null) { // disable impact damage if piggybacking
                    return false;
                } else if (!netBody.IsPlayer) {// fuck it, just make npcs take damage to prevent unsyncability issues or idk what im doing rn
                    netBody.RollDamageRandom();
                    return true;
                }
            }


            if (!last_impact_is_local_body) { // its not me i dont control their physics, override, strip only sounds and effects from the function

                if (Net.IsServer) {
                    return false; // host receives the ImpactDamage request anyway
                }

                //OG: Limb.ImpactDamage
                var l = __instance;
                if (!(force > body.jumpSpeed)) {
                    return false; // i wanna override the og anyway
                }



                if (force > body.jumpSpeed * 1.66f) {
                    body.DoFurTuft();
                }
                if (force > UnityEngine.Random.Range(body.jumpSpeed * 1.55f, body.jumpSpeed * 4.4f) * ((l.muscleHealth < 50f) ? 0.632f : 1f)) {
                    if (force > body.jumpSpeed * 2.6f) {
                        body.DoGoreSound();
                    }
                }
                Vector2 pos = (Vector2)l.transform.position + dir;
                ushort block = WorldGeneration.world.GetBlock(pos);
                if ((float)(int)block > 0f) {
                    Sound.Play(WorldGeneration.world.RandomStepSound(WorldGeneration.world.GetBlockInfo(block).stepsound), pos);
                }
                if (force > 3f && body.soundCooldown <= 0f) {
                    body.soundCooldown = 0.25f;
                    Sound.Play($"bodyFall{UnityEngine.Random.Range(1, 6)}", l.transform.position);
                }
                return false;
            }
        }


        if (__instance.TryGetNetBody(out var nb)) {
            nb.RollDamageRandom();
        }

        return true;
    }

    static void Postfix(Limb __instance, float force, Vector2 dir) {
        if (!Net.IsRunning) return;

        if (!force_run && __instance.IsBodyLocal() && Net.IsClient && Util.IsWorldGenerated()) {
            var body = __instance.body;


			//OG: Limb.ImpactDamage
			if (!(force > body.jumpSpeed)) {
                return;
            }

            if (body.TryGetNetBody(out var nb)) {
                nb.SetNetHealthSyncIgnoreTime();
                if (nb.piggybacking_on) { // disable impact damage if piggybacking
                    return;
                }
            }



            // tell server about this
            var writer = Net.CreateWriter(NetmsgId.PlayerImpactDamage);

            writer.Put((byte)Util.GetLimbIndex(__instance));
            writer.Put(force);
            writer.Put(dir);
            GameObject hitFrom = Limb_OnCollisionEnter2D_MultiplayerPatch.CurrentCollision != null ? Limb_OnCollisionEnter2D_MultiplayerPatch.CurrentCollision.gameObject : null;

            writer.Put(AnyObjectNetId.GetNetIdFromGameObject(hitFrom));


            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
#if DEBUG
            Plugin.log.LogInfo($"Sending PlayerImpactDamage  {force} {dir}  ");
#endif

            // WRITER END;

        }
        force_run = false;

    }

}





















internal class MedicalSync : CasualtiesTogetherSingleton {
    static bool stolenformp_lastHeartBeating = false;
    static readonly Dictionary<knetid, CharacterHealthStateSyncPacket> HeldHealth = new Dictionary<knetid, CharacterHealthStateSyncPacket>();
    static readonly List<knetid> HeldHealthDone = new List<knetid>();

    // latest body snapshot per id. applied once this client is in the game and that body exists. no expiry.
    static readonly HashSet<knetid> ForceHeld = new HashSet<knetid>();

    internal static void HoldHealth(knetid id, CharacterHealthStateSyncPacket health, bool force = false)
    {
        if (force)
            ForceHeld.Add(id);
        if (KrokV5Optimization.FixInvSync.InGame() && NetBody.TryGetNetBodyFromId(id, out NetBody nb) && nb.body != null)
        {
            bool confirm = force || ForceHeld.Remove(id);
            health.Apply(nb, confirm);
            return;
        }
        HeldHealth[id] = health;
    }

    internal static void FlushHeldHealth()
    {
        if (!Util.IsInWorld())
        {
            HeldHealth.Clear();
            ForceHeld.Clear();
            return;
        }
        if (!KrokV5Optimization.FixInvSync.InGame() || HeldHealth.Count == 0)
            return;
        HeldHealthDone.Clear();
        foreach (KeyValuePair<knetid, CharacterHealthStateSyncPacket> pair in HeldHealth)
        {
            if (!NetBody.TryGetNetBodyFromId(pair.Key, out NetBody nb) || nb.body == null)
                continue;
            pair.Value.Apply(nb, ForceHeld.Remove(pair.Key));
            HeldHealthDone.Add(pair.Key);
        }
        for (int i = 0; i < HeldHealthDone.Count; i++)
            HeldHealth.Remove(HeldHealthDone[i]);
    }

    public static bool IsRefusingHelp(Body b) {
        return !b.aboveMedicalCutoff && b.conscious ;
    }

    internal static ECGVisualizer woundview_ecg = null;
    void LateUpdate() {

        if (!Util.IsInWoundView()) {
            stolenformp_lastHeartBeating = false;
            return; 
        }
            
        // override woundview stuff

        var body = WoundView.view.body;

        // stop the breath sound when theyre dead 
        if (body) {
            var alert_and_breath_audio = WoundView.view.GetComponent<AudioSource>();
            bool antidispersion = false;

            try {// override ecg
                if (woundview_ecg == null) {
                    woundview_ecg = WoundView.view.gameObject.GetComponentInChildren<ECGVisualizer>(true);
                }
                var oldfollowbody = woundview_ecg.followBody;
                if (body.IsBodyLocal()) {
                    woundview_ecg.followBody = true;
                } else {
                    woundview_ecg.followBody = false;
                    woundview_ecg.writeHeight = body.GetECGHeight(woundview_ecg.timeToUpdate - 0.028f);
                }
                if (woundview_ecg.followBody != oldfollowbody) { // reset the ecg screen
                    for (int j = 0; j < woundview_ecg.width; j++) {
                        for (int k = 0; k < woundview_ecg.height; k++) {
                            Color32 color = woundview_ecg.pixelGrid[j, k];
                            color.a = 0;
                            woundview_ecg.pixelGrid[j, k] = color;
                        }
                    }
                }
            } catch (Exception e) {
                log.error("WOUNDVIEW ECG OVERRIDE: " + e.ToString());
            }


            if (!body.alive) {
                stolenformp_lastHeartBeating = false;
                Util.WoundViewShowAlertText(Lang.Get("wv_deceased"));
            } else {
                {
                    if (stolenformp_lastHeartBeating && body.inCardiacArrest) {
                        stolenformp_lastHeartBeating = false;
                        if (!Util.IsUnchipped() && !body.IsBodyLocal()) {
                            Sound.Play("flatline", Vector2.zero, twoDimensional: true, pitchShift: false, null, 0.8f, 1f, noReverb: true, ignoreMixer: true);
                        }
                    }
                    stolenformp_lastHeartBeating = !(body.inCardiacArrest);
                }

                if (SharedMain.CheckIfDispersionPunishmentProtocolRuleIsActive()) {
                    var targetplr = ScavPlayer.LocalPlayer;
                    if (UIInGame.SpectatorModeActive && body.TryGetNetBody(out var pb)) {
                        targetplr = pb.plr;
                    }
                    if (Util.IsBodyLocal(body) && targetplr != null) {


                        if (SharedMain.CheckIfShouldActivateDispersionPunishmentProtocol(targetplr, out var max_recorded_dist, out var count_plrs_in_range, out var required_count_plrs_in_range)) {
                            var thing = (int)Time.unscaledTime % 3;

                            antidispersion = true;

                            string res;
                            switch (thing) {
                                case 0:
                                res = Lang.Get("healthchipalert_antidispersion1");
                                break;
                                case 1:
                                res = Locale.GetOther("healthchipradalert2"); // punishment protocol active 
                                break;
                                case 2:
                                res = Lang.Get("healthchipalert_antidispersion3");
                                break;
                                default:
                                res = Locale.GetOther("healthchipradalert2"); // punishment protocol active 
                                break;
                            }

                            Util.WoundViewShowAlertText(res);
                        } else {
                            if (!Util.WoundViewAlertIsShown()) {
                                if (
                                    //(NetPlayer.BodyToPlayerDict.Count > 4 && (float)count_plrs_in_range < Mathf.Max(1f, (float)required_count_plrs_in_range * 0.9f - 1f)) ||
                                    (
                                        max_recorded_dist > Util.MetersToTiles(Multiplayer.rules.ScatterPunishDistance) * 0.8f //Mathf.Clamp((10 - NetPlayer.BodyToPlayerDict.Count) * 0.1f, 0.7f,0.95f)
                                    )
                                ) {
                                    var thing = (int)Time.unscaledTime % 2;

                                    string res;
                                    switch (thing) {
                                        case 0:
                                        res = Lang.Get("healthchipalert_antidispersion_warn"); // peer signal is weak 
                                        break;
                                        case 1:
                                        default:
                                        res = Lang.Get("healthchipalert_antidispersion3"); // stay together 
                                        break;
                                    }

                                    Util.WoundViewShowAlertText(res);
                                }
                            }
                        }
                    }
                }


            }

            alert_and_breath_audio.volume = (body.alive || antidispersion) ? 1 : 0;

        }
        
    }


    public static bool TryGetPlayerAndLimb(knetid playerId, byte limbId, out NetBody nb, out Limb limb) {
        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(playerId, out var plr, out nb) && limbId < nb.body.limbs.Count()) {
            limb = nb.body.limbs[limbId];
            return true;
        }
        limb = null;
        return false;
    }
    public static bool TryGetNetBodyAndLimb(knetid netBodyId, byte limbId, out NetBody nb, out Limb limb) {
        if (NetBody.TryGetNetBodyFromId(netBodyId, out nb ) && limbId < nb.body.limbs.Count()) {
            limb = nb.body.limbs[limbId];
            return true;
        }
        limb = null;
        return false;
    }


    public static bool Server_CheckCanHealerUseItemForLimb(knetid healer_CId, knetid target_bodyNetId, byte limb_id, knetid item_syncid, out ScavPlayer healersci, out NetBody target_nb, out SyncInfo item_si) {
        healersci = null;
        target_nb = null;
        item_si = null;

        var intrdist = SharedMain.max_player_interaction_distance * 2.5f;

        var res = ScavPlayer.TryGetPlayerAndBodyFromClientId(healer_CId, out healersci, out var healerbody) && healerbody.conscious &&
                TryGetNetBodyAndLimb(target_bodyNetId, limb_id, out target_nb, out var targetlimb ) &&
                NetObjectRegistry.TryGetSyncInfo(item_syncid, out item_si) && item_si.IsItem() && (item_si.item.Stats.usableOnLimb) &&
                ItemSync.CheckIfBodyReachThisItem(item_si.item, healerbody, intrdist, false) &&
                Util.DoFullInteractionCheck(healerbody, target_nb.body, false, intrdist, false);
        
        return res;
    }



    
    public static void Server_SendCharacterHealth(NetBody nb, bool force = false) {
        if ((force || MedicalSync.CanSyncHealth()) ) {
            
            Body cur_body = nb.body;

            var health = new CharacterHealthStateSyncPacket();
            health.Write(cur_body);

            var writer = Net.CreateWriter(NetmsgId.CLIENT_HealthSync);

            writer.Put(nb.netId);
            writer.Put(health);
            writer.Put(nb.healthConfirm);
            nb.healthConfirm = false;

            writer.CompressWriter();

            Net.Server_SendToClients( DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);

            // WRITER END;
        }
    }

    // where its not really important to send character health right now and doesnt even matter if it arrives
    
    static Queue<NetBody> server_tosend_queue = new Queue<NetBody>();
    public static void Server_QueueSendCharacterHealth(NetBody nb, bool force = false) {
        Con.ConFailIfNetworkIsRunningAndIsClient();

        if (force || (MedicalSync.CanSyncHealth() && !nb._SERVER_healthsync_skip_frame)) {
            if (force)
                nb.healthConfirm = true;
            nb._SERVER_healthsync_skip_frame = true;
            server_tosend_queue.Enqueue(nb);

        }
    }



    public static bool CanSyncHealth() {
        if (ServerMain._DEV_ENABLE_HP_SYNC) {
            return true;
        }
        return false;
    }









    // health is server-authoritative,  this means client also listens about what it should feel,  client only does physics impacts
    static int _mischealthsync_cur_body = 0;
    static int _healthsync_cur_body = 0;
    private IEnumerator HealthUpdateSyncClients() {

        if ( Util.IsWorldGenerated() && Net.IsRunning && MedicalSync.CanSyncHealth()) {
            
            { // shouldnt be here but whatever i needed to sync the painkiller state
                var tosync = new List<NetBody>();
                int bytecounter = 0;

                LiteNetLib.Utils.NetDataWriter writer;

                // SYNC MISC STATES LIKE PAINKILLER 
                {

                    int fullpacksize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(CharacterHealthPainkillerStateSyncPacket)) + 8 + 2;


                    writer = Net.CreateWriter(NetmsgId.CLIENT_HealthPainkillerSync);

                    while (true) {

						var dobreak = false;
                        if (_mischealthsync_cur_body >= NetBody.all_instances.Count()) {
                            _mischealthsync_cur_body = 0;
                            dobreak = true;
                        }
                        if (bytecounter + fullpacksize >= 1000) {
                            // next packet will go beyond max udp packet size
                            dobreak = true;
                        }

                        if (dobreak) {

							timer_HealthUpdateSyncClients = -5;
							yield return null;
                            tosync.RemoveAll(x => x.body == null);

                            // send the packet
                            if (tosync.Count > 0) {

                                writer.SetPosition(sizeof(NetmsgId)); // set position to 0 (i am reusing same writer for multiple packets)

                                writer.Put(tosync[0].netId);
                                writer.Put(new CharacterHealthPainkillerStateSyncPacket(tosync[0].body));
                                for (int i = 1; i < tosync.Count; i++) {
                                    writer.Put(true);
                                    writer.Put(tosync[i].netId);
                                    writer.Put(new CharacterHealthPainkillerStateSyncPacket(tosync[i].body));
                                }
                                writer.Put(false);

                                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                                
                                bytecounter = 0;
                                tosync.Clear();
                            }


                            break;
                        }

                        var nb = NetBody.all_instances[_mischealthsync_cur_body];
                        _mischealthsync_cur_body++;

                        if (nb._SERVER_healthsync_skip_frame || nb.body == null || !nb.body.TryGetComponent<Painkillers>(out var _)) {
                            continue;
                        }

                        tosync.Add(nb);
                        bytecounter += fullpacksize;

                        yield return null; // wait a frame
                    }

                    // WRITER END;

                }
            }


            while (true) {
				if (_healthsync_cur_body >= NetBody.all_instances.Count()) {
                    _healthsync_cur_body = 0;
                    break;
                }

                var npc = NetBody.all_instances[_healthsync_cur_body];
                _healthsync_cur_body++;

                if (npc._SERVER_healthsync_skip_frame) {
                    npc._SERVER_healthsync_skip_frame = false;
                    _healthsync_cur_body++;
                    continue; 
                } 
                if (!npc.body.alive) {
                    npc._SERVER_healthsync_skip_frame = !npc._SERVER_healthsync_skip_frame;
                }

                Server_SendCharacterHealth(npc);

                yield return null;
            }

        }


        yield return null;

        timer_HealthUpdateSyncClients = 0;
    }








    private void Start() {

    }

    public const float SyncFrequencyHealth = 0.9124532513f;

    public float timer_HealthUpdateSyncClients = 0;
    protected void Update() {
        if (Net.IsClient) {
            FlushHeldHealth();
            return;
        }

        if (server_tosend_queue.Count > 0) {
            var nb = server_tosend_queue.Dequeue();
            if (nb != null) {
                nb._SERVER_healthsync_skip_frame = true;
                Server_SendCharacterHealth(nb, true);
            }
        }


        timer_HealthUpdateSyncClients += Time.unscaledDeltaTime;

        if (timer_HealthUpdateSyncClients > SyncFrequencyHealth) {
            timer_HealthUpdateSyncClients = -2 * NetBody.all_instances.Count;
            StartCoroutine(HealthUpdateSyncClients());
        }
    }
































    [ClientReceiver((byte)NetmsgId.CLIENT_HealthPainkillerSync)]
    static void ClientReceiver__HealthPainkillerSync(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        
        reader.Get(out knetid who);
        reader.Get(out CharacterHealthPainkillerStateSyncPacket health);
        if (NetBody.TryGetNetBodyFromId(who, out var nb)) {
            health.Apply(nb.body);
        }

        while (true) {
            reader.Get(out bool next);
            if (next) {
                reader.Get(out knetid who1);
                reader.Get(out CharacterHealthPainkillerStateSyncPacket health1);

                if (NetBody.TryGetNetBodyFromId(who, out var nb1)) {
                    health1.Apply(nb1.body);
                }
            } else {
                break;
            }
        }

    }





    [ClientReceiver((byte)NetmsgId.CLIENT_HealthSync)]
    static void ClientReceiver__HealthSync(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader = reader.DecompressReader();

        reader.Get(out knetid NPC_ID);

        reader.Get(out CharacterHealthStateSyncPacket health);
        bool confirm = false;
        if (reader.AvailableBytes >= 1)
            reader.Get(out confirm);
        HoldHealth(NPC_ID, health, confirm);

    }



    [ClientReceiver((byte)NetmsgId.CLIENT_VomiterVomit)]
    static void ClientReceiver__VomiterVomit(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        
        reader.Get(out knetid who);
        reader.Get(out bool isblood);

        if (ScavPlayer.GetBodyFromClientId(who) is var body && body != null) {
            Vomiter_Vomit_MultiplayerPatch.ForceNoWarning(body.vomiter, isblood);
        }

    }














    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_WoundSpecialAction)]
    static void ServerReceiver_WoundSpecialAction(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        if (!CanSyncHealth()) {
            return;
        }

        reader.Get(out LimbNetId target_limb);

        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var healer, out var healerbody) && healer.body.conscious &&
            target_limb.TryGetNetBodyAndLimbSafe(out var target_pb, out var limb) 
        ) {
            var targetbody = target_pb.body;
            if (healerbody != targetbody) {
                if (!KM.DistanceCheck(healer.body, targetbody, SharedMain.max_player_interaction_distance * 1.3f)) {
                    return;
                }
                if (!Util.AccurateRaycastInteractionCheckObstruction(healer.body, targetbody, false)) {
                    return;
                }
            }

            if (!healer.IsLocal) {
                PlayerCamera_WoundSpecialAction_MultiplayerPatch.OG_WoundSpecialAction(healer.body, limb);
            }

            log.devevent($"S: WoundSpecialAction {healer} -> {target_limb}   ", limb.transform.position);

            Server_QueueSendCharacterHealth(target_pb);

        } else {
            log.sus($"{DebugHelp.GetPlayerFullDebugString(clientId)} WoundSpecialAction on an invalid limb");
        }


    }






    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_ApplyWoundItem)]
    static void ServerReceiver_ApplyWoundItem(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        if (!CanSyncHealth()) {
            return;
        }

        reader.Get(out LimbNetId targetlimb);
        reader.Get(out knetid item_syncid);

        if (
            ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var healer, out var healer_nb) && healer.body.conscious &&
            targetlimb.TryGetNetBodyAndLimbSafe(out var target_nb, out var limb) &&
            ItemSync.TryGetItemSyncInfo(item_syncid, out var isi) 
        ) {

#if !RELEASE // these are for verbose output why shit gets denied
            // all of this gets checked in the DoApplyWoundItemChecks
            if (healer_nb != target_nb) {
                if (!KM.DistanceCheck(healer.body, target_nb.body, SharedMain.max_player_interaction_distance * 1.3f)) {
                    Plugin.log.LogWarning($"Server receives: PlayerCamera.ApplyWoundItem FAILED: TOO FAR {healer} -> {targetlimb} {isi}  ");
                    return;
                }
                if (!Util.AccurateRaycastInteractionCheckObstruction(healer.body, target_nb.body, false)) {
                    Plugin.log.LogWarning($"Server receives: PlayerCamera.ApplyWoundItem FAILED: OBSTRUCTED {healer} -> {targetlimb} {isi}  ");
                    return;
                }
            }
#endif 

            // but this is needed here
            if (!ItemSync.CheckIfBodyReachThisItem(isi, healer.body, 20f, true)) {
                Plugin.log.LogWarning($"Server receives: PlayerCamera.ApplyWoundItem FAILED: CANT REACH {healer} -> {targetlimb} si:{isi}  ");
                return;
            }


            if (PlayerCamera_ApplyWoundItem_MultiplayerPatch.DoApplyWoundItemChecks(healer.body, limb, isi.item)) {



                // tell everyone to use this item (so that visuals and sounds apply)
                var writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerCamera_ApplyWoundItem_Relay);

                writer.Put(clientId);
                writer.Put(targetlimb);
                writer.Put(item_syncid);

                if (log.verbose) log.l($"Server receives: PlayerCamera.ApplyWoundItem {healer.playerName} -> {targetlimb} si:{isi}  ");

                Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
                // WRITER END;


                MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = !Util.IsBodyLocal(healer.body);
                //OG: PlayerCamera.ApplyWoundItem
                PlayerCamera_ApplyWoundItem_MultiplayerPatch.ForceApplyWoundItem(healer.body, limb, isi.item);
                MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = false;



                Server_QueueSendCharacterHealth(target_nb);


            } else {

                Plugin.log.LogWarning($"Server receives: PlayerCamera.ApplyWoundItem FAILED {healer} -> {targetlimb} {isi}  ");
            }
        }


    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerCamera_ApplyWoundItem_Relay)]
    static void ClientReceiver__ApplyWoundItem_Relay(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        reader.Get(out knetid who_clientId);
        reader.Get(out LimbNetId targetlimb);
        reader.Get(out knetid item_syncid);

        if (
            targetlimb.TryGetNetBodyAndLimb(out var target_nb, out var target_limb) &&
            ScavPlayer.TryGetPlayerAndBodyFromClientId(who_clientId, out var healer, out var healer_body) &&
            healer.IsLocal && // check if its not me, cuz on my screen ive already done it visually, even if im host it was still done server side
            NetObjectRegistry.TryGetSyncInfo(item_syncid, out var si)
        ) {
            MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = true;
            PlayerCamera_ApplyWoundItem_MultiplayerPatch.ForceApplyWoundItem(healer_body, target_limb, si.item);
            MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = false;

#if !RELEASE
            Plugin.log.LogInfo($"RECEIVED PlayerCamera.ApplyWoundItem_Relay {targetlimb} ");
#endif
        }

    }




















}



