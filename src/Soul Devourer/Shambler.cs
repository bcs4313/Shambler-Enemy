using GameNetcodeStuff;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Vector3 = UnityEngine.Vector3;
using Quaternion = UnityEngine.Quaternion;
using Shambler;
using System.Security.Cryptography;
using Shambler.src.Soul_Devourer;
using System.Threading.Tasks;
using LethalLib.Modules;

namespace SoulDev
{
    class ShamblerEnemy : EnemyAI
    {
        protected Animator animator;

        // nest logic
        Vector3 nestSpot = Vector3.zero;
        public static List<ulong> stuckPlayerIds = new List<ulong>();

        // leap variables
        protected float baseLeapChance = 100;
        protected float leaptime = 0;
        protected Vector3 leapPos;

        // related to entering and exiting entrances (updated ~4s)
        protected EntranceTeleport nearestEntrance = null;
        public Vector3 nearestEntranceNavPosition = Vector3.zero;
        protected PlayerControllerB mostRecentPlayer = null;
        protected int entranceDelay = 0;  // prevents constant reentry / exit
        protected float chanceToLocateEntrancePlayerBonus = 0;
        protected float chanceToLocateEntrance = 0;

        // updated once every 15 seconds
        public List<EnemyAI> unreachableEnemies = new List<EnemyAI>();
        public Vector3 itemNavmeshPosition = Vector3.zero;
        protected int sourcecycle = 75;

        // stamina mechanics
        protected float stamina = 0; // shamblers use stamina to chase the player
        protected bool recovering = false; // no chase if recovering
        public int provokePoints = 0;

#pragma warning disable 0649
        public Transform turnCompass;
#pragma warning restore 0649
        protected float timeSinceHittingLocalPlayer;
        protected float timeSinceNewRandPos;
        protected Vector3 positionRandomness;
        protected Vector3 StalkPos;
        protected System.Random enemyRandom;
        protected bool isDeadAnimationDone;

        // custom sounds
        bool markDead = false;

        // custom audio sources
        public AudioSource creatureLeapLand;
        public AudioSource creatureAnger;
        float angerSoundTimer = 0f;
        public AudioSource creatureLaugh;
        public AudioSource creaturePlant;
        public AudioSource creatureSneakyStab;
        public AudioSource creatureDeath;
        public AudioSource creatureTakeDmg;
        public AudioSource[] creatureSteps;

        // animation vars (new)
        protected float runAnimationCoefficient = 14f;
        protected float walkAnimationCoefficient = 3f;

        public enum State
        {
            SearchingForPlayer,
            HeadingToEntrance,
            Crouching,          // prepare for a leap
            Leaping,            // jump on the player!
            ClosingDistance,    // attempt to sneak up on the player
            StabbingCapturedPlayer,
            HeadingToNest,
            PlantingStake,
            SneakyStab  // stab the player in the back!
        }

        // --- AI debug + control ---
        [Header("AI Debug")]
        public bool debugThoughts = true;
        public bool debugDrawGoal = true;
        private Vector3 lastGoal = Vector3.zero;
        private float lastGoalScore = 0f;
        // When true we are explicitly following our own waypoint (not base chase)
        private bool usingCustomGoal = false;

        private void Think(string msg)
        {
            if (!debugThoughts) return;
            Debug.Log($"[Shambler@{Time.time:F1}] {msg}");
        }

        private void OnDrawGizmosSelected()
        {
            if (!debugDrawGoal || lastGoal == Vector3.zero) return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(transform.position + Vector3.up * 0.5f, lastGoal + Vector3.up * 0.5f);
            Gizmos.DrawSphere(lastGoal, 0.25f);
        }

        public void LogDebug(string text)
        {
#if DEBUG
            Plugin.Logger.LogInfo(text);
#endif
        }

        public void facePosition(Vector3 pos)
        {
            Vector3 directionToTarget = pos - transform.position;
            directionToTarget.y = 0f; // Ignore vertical difference
            if (directionToTarget != Vector3.zero)
            {
                Quaternion targetRotation = Quaternion.LookRotation(directionToTarget);
                transform.rotation = Quaternion.Euler(0f, targetRotation.eulerAngles.y, 0f);
            }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
        }

        public PlayerControllerB getNearestPlayer(bool addFilter = true)
        {
            var players = RoundManager.Instance.playersManager.allPlayerScripts;

            PlayerControllerB bestPlayer = null;
            float bestDistance = 999999999f;
            for (int i = 0; i < players.Length; i++)
            {
                var player = players[i];
                if (player && !player.isPlayerDead && player.isPlayerControlled)
                {
                    float d = Vector3.Distance(this.transform.position, player.transform.position);
                    if (d < bestDistance)
                    {
                        // player must follow a lot of rules to qualify here!
                        if ((PlayerQualifies(player)) || !addFilter)
                        {
                            bestDistance = d;
                            bestPlayer = player;
                        }
                    }
                }
            }
            return bestPlayer;
        }

        public void SetAudioVolumes()
        {
            /*
            creatureAnger.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creatureDeath.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creatureLaugh.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creatureLeapLand.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creaturePlant.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creatureSFX.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creatureSneakyStab.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creatureStab.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creatureTakeDmg.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            creatureVoice.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            foreach(var aud in creatureSteps)
            {
                aud.volume = creatureAnger.volume *= (Plugin.moaiGlobalMusicVol.Value / 0.6f);
            }
            */
        }

        public override void Start()
        {
            base.Start();

            if(RoundManager.Instance.IsHost && FindObjectsOfType<ShamblerEnemy>().Length > Plugin.maxCount.Value)
            {
                Destroy(this.gameObject);
                Debug.Log("Shambler: Destroyed self (enemy count too high)");
            }

            // based on scaling (if applicable)
            maxStabDistance = 5f * transform.localScale.x;
            maxLeapDistance = 20f * transform.localScale.x;
            captureRange = 5.35f * transform.localScale.x;

            // sets up a nest whereever he spawns
            // the nest is where spikes are placed
            nestSpot = this.transform.position;

            EntityWarp.mapEntrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>(false);
            mostRecentPlayer = getNearestPlayer();
            animator = this.gameObject.GetComponent<Animator>();

            if (RoundManager.Instance.IsHost)
            {
                this.DoAnimationClientRpc(0);
            }

            stamina = 120;

            timeSinceHittingLocalPlayer = 0;
            timeSinceNewRandPos = 0;
            positionRandomness = new Vector3(0, 0, 0);
            enemyRandom = new System.Random(StartOfRound.Instance.randomMapSeed + thisEnemyIndex);
            isDeadAnimationDone = false;

            currentBehaviourStateIndex = (int)State.SearchingForPlayer;
            StartSearch(transform.position);
            moaiSoundPlayClientRpc("creatureVoice");

            this.enemyHP = Plugin.health.Value;

            // Build world mask to use for hiding and LOS
            WorldMask = BuildWorldMask();
            Think($"WorldMask={WorldMask.value} built.");

            // Sanity LOS test log
            var np = getNearestPlayer();
            if (np)
            {
                var from = np.playerEye ? np.playerEye.transform.position : np.transform.position + Vector3.up * 1.6f;
                var to = transform.position + Vector3.up * 1.2f;
                bool blocked = Physics.Linecast(from, to, out var hit, WorldMask, QueryTriggerInteraction.Ignore);
                Think($"Initial LOS blocked={blocked}, hit={(hit.collider ? hit.collider.name : "none")}, layer={(hit.collider ? LayerMask.LayerToName(hit.collider.gameObject.layer) : "none")}");
            }

            // locking to face player in these instances
            if (currentBehaviourStateIndex == (int)State.Leaping && targetPlayer)
            {
                facePosition(targetPlayer.transform.position);
            }
        }

        private static bool IsPlayerStaked(PlayerControllerB p)
        {
            if (p == null || p.NetworkObject == null) return false;
            return stuckPlayerIds.Contains(p.NetworkObject.NetworkObjectId);
        }

        float sizeCheckCooldown = 2f;
        bool stepSoundCycle1 = false;
        bool stepSoundCycle2 = false;
        public override void Update()
        {
            base.Update();

            sizeCheckCooldown -= Time.deltaTime;
            if (sizeCheckCooldown < 0)
            {
                // scaling limit
                if (this.transform.localScale.x > 1 || this.transform.localScale.y > 1 || this.transform.localScale.z > 1)
                {
                    this.transform.localScale = new Vector3(1f, 1f, 1f);

                    // based on scaling (if applicable)
                    maxStabDistance = 5f * transform.localScale.x;
                    maxLeapDistance = 20f * transform.localScale.x;
                    captureRange = 5.35f * transform.localScale.x;
                }
                sizeCheckCooldown = 2f;
            }
            


            // death check for traps
            if (!this.isEnemyDead && enemyHP <= 0 && !markDead)
            {
                this.animator.speed = 1;
                base.KillEnemyOnOwnerClient(false);
                this.stopAllSound();
                if (!animator.GetCurrentAnimatorStateInfo(0).IsName("Death") && !animator.GetCurrentAnimatorStateInfo(0).IsName("Exit"))
                {
                    animator.Play("Death");
                }
                isEnemyDead = true;
                enemyHP = 0;
                moaiSoundPlayClientRpc("creatureDeath");
                deadEventClientRpc();
                markDead = true;
                GetComponent<BoxCollider>().enabled = false;
            }

            if(this.isEnemyDead) 
            { 
                if(stabbedPlayer) 
                {
                    //stabbedPlayer.playerCollider.enabled = true;
                    stabbedPlayer = null;
                }
                if (capturedPlayer)
                {
                    //capturedPlayer.playerCollider.enabled = true;
                    capturedPlayer = null;
                }

                // dead idle
                if(!animator.GetCurrentAnimatorStateInfo(0).IsName("Death") && !animator.GetCurrentAnimatorStateInfo(0).IsName("StayDead"))
                {
                    animator.Play("StayDead");
                }

                return; 
            }

            // loop to ensure captured and stabbed players don't randomly die
            if (stabbedPlayer)
            {
                if (stabbedPlayer.playerCollider.enabled && !stabbedPlayer.isPlayerDead)
                {
                    //SetColliderClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId, false);
                }
                stabbedPlayer.fallValue = 0;
                stabbedPlayer.fallValueUncapped = 0;

                if (stabbedPlayer.isPlayerDead)
                {
                    //SetColliderClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId, true);
                }
            }

            // loop to ensure captured and stabbed players don't randomly die
            // and to renable the colliders of dead players
            if (capturedPlayer)
            {
                if (capturedPlayer.playerCollider.enabled && !capturedPlayer.isPlayerDead)
                {
                    //SetColliderClientRpc(capturedPlayer.NetworkObject.NetworkObjectId, false);
                }
                capturedPlayer.fallValue = 0;
                capturedPlayer.fallValueUncapped = 0;

                if (capturedPlayer.isPlayerDead)
                {
                    //SetColliderClientRpc(capturedPlayer.NetworkObject.NetworkObjectId, true);
                }
            }

            if (isEnemyDead)
            {
                if (!isDeadAnimationDone)
                {
                    this.animator.speed = 1;
                    isDeadAnimationDone = true;
                    stopAllSound();
                    creatureVoice.PlayOneShot(dieSFX);
                }
                return;
            }

            // step sounds
            var state = animator.GetCurrentAnimatorStateInfo(0);

            if (state.IsName("Walk"))
            {
                // wrap into [0,1)
                float loopT = state.normalizedTime - Mathf.Floor(state.normalizedTime);

                if (loopT < 0.10f)
                {
                    stepSoundCycle1 = false;
                    stepSoundCycle2 = false;
                }
                if (loopT > 0.15f && !stepSoundCycle1)
                {
                    moaiSoundPlayClientRpc("step");
                    stepSoundCycle1 = true;
                }
                if (loopT > 0.50f && !stepSoundCycle2)
                {
                    moaiSoundPlayClientRpc("step");
                    stepSoundCycle2 = true;
                }
            }
            else
            {
                // if we left Walk, clear the latches so they’re ready next time
                stepSoundCycle1 = stepSoundCycle2 = false;
            }


            // anger sound with cooldown
            if (alertLevel > sneakyAlertLevel && RoundManager.Instance.IsHost)
            {
                if (angerSoundTimer <= 0)
                {
                    angerSoundTimer = 5;
                    moaiSoundPlayClientRpc("creatureAnger");
                }
            }

            if (angerSoundTimer >= 0)
            {
                angerSoundTimer -= Time.deltaTime;
            }

            if (targetPlayer != null && targetPlayer.isPlayerDead) { targetPlayer = null; }

            // CRITICAL: do not allow base chase override when we are using a custom goal
            movingTowardsTargetPlayer = (targetPlayer != null) && !usingCustomGoal;

            timeSinceHittingLocalPlayer += Time.deltaTime;
            timeSinceNewRandPos += Time.deltaTime;
            if (targetPlayer != null && PlayerIsTargetable(targetPlayer))
            {
                turnCompass?.LookAt(targetPlayer.gameplayCamera.transform.position);
            }

            // stun mechanic?
            if (stunNormalizedTimer > 0f && RoundManager.Instance.IsHost)
            {
                // optional
            }
        }

        // capture code
        void LateUpdate()
        {
            if (this.isEnemyDead) { return; }

            if (capturedPlayer)
            {
                capturedPlayer.transform.position = capturePoint.position;
            }

            if (stabbedPlayer)
            {
                //Think("Set player to stab position: " + stabPoint.position);
                stabbedPlayer.transform.position = stabPoint.position;
            }
            timeTillStab -= Time.deltaTime;
        }

        public override void DoAIInterval()
        {
            base.DoAIInterval();

            if (this.isEnemyDead) { return; }

            if (provokePoints > 0) provokePoints--;

            if (spottedCooldown > 0f)
                spottedCooldown = Mathf.Max(0f, spottedCooldown - 1f);

            if (entranceDelay > 0) entranceDelay--;

            // source update cycle
            if (sourcecycle > 0)
            {
                sourcecycle--;
            }
            else
            {
                sourcecycle = 75;
                unreachableEnemies.Clear();
            }

            if (stamina <= 0) recovering = true;
            else if (stamina > 60) recovering = false;

            // executes once every second
            if (sourcecycle % 5 == 0)
            {
                var ePack = EntityWarp.findNearestEntrance(this);
                nearestEntrance = ePack.tele;
                nearestEntranceNavPosition = ePack.navPosition;

                if (stamina < 120) stamina += 8;  // ~10s full regen
                mostRecentPlayer = getNearestPlayer();
            }

            if (targetPlayer != null) mostRecentPlayer = targetPlayer;

            AIInterval();

            // alert level decay & clamp
            if (alertLevel > 0) alertLevel = Mathf.Max(0f, alertLevel - alertDecay);
            alertLevel = Mathf.Min(100f, alertLevel);
        }

        public void AIInterval()
        {
            // transition to stabbing a player if one is held, and the player
            // is not stabbed
            if (capturedPlayer && !stabbedCapturedPlayer && timeTillStab < 0 &&
                currentBehaviourStateIndex != (int)State.StabbingCapturedPlayer)
            {
                StopSearch(currentSearch);
                SwitchToBehaviourClientRpc((int)State.StabbingCapturedPlayer);
                isStabbing = false;
                doneStab = false;
            }

            switch (currentBehaviourStateIndex)
            {
                case (int)State.SearchingForPlayer:
                    usingCustomGoal = false; // ensure base can own movement
                    baseSearchingForPlayer();
                    break;
                case (int)State.HeadingToEntrance:
                    usingCustomGoal = false; // ensure base can own movement
                    baseHeadingToEntrance();
                    break;
                case (int)State.Crouching:
                    baseCrouching();
                    break;
                case (int)State.ClosingDistance:
                    baseClosingDistance();
                    break;
                case (int)State.Leaping:
                    baseLeaping();
                    break;
                case (int)State.StabbingCapturedPlayer:
                    baseStabbingCapturedPlayer();
                    break;
                case (int)State.HeadingToNest:
                    baseHeadingToNest();
                    break;
                case (int)State.PlantingStake:
                    basePlantingStake();
                    break;
                case (int)State.SneakyStab:
                    baseSneakyStab();
                    break;
                default:
                    LogDebug("This Behavior State doesn't exist!");
                    break;
            }
        }

        public float stabTimeout = 6f;
        public void baseSneakyStab()
        {
            //Think("sneakystab");
            agent.speed = 0f;
            stabTimeout -= 0.2f;
            setAnimationSpeedClientRpc(1);
            agent.updateRotation = false;

            if (getNearestPlayer())
            {
                facePosition(getNearestPlayer().transform.position);
            }

            if (isEnemyDead)
            {
                agent.updateRotation = true;
                isStabbing = false;
                doneStab = false;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }

            // call routine and wait for it to be done
            if (!isStabbing)
            {
                StartCoroutine(DoSneakyStab());
                stabTimeout = 6f;
                isStabbing = true;
            }
            else if (doneStab)
            {
                // do various things based on the result of the stab
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                isStabbing = false;
                doneStab = false;
                if (!stabbedPlayer)
                {
                    stabbedPlayer = getNearestPlayer();
                    if (stabbedPlayer != null) { SetStabbedPlayerClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId); }
                }
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }
            else if (stabTimeout < 0)
            {
                // do various things based on the result of the stab
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                isStabbing = false;
                doneStab = false;
                if (!stabbedPlayer)
                {
                    stabbedPlayer = getNearestPlayer();
                    if (stabbedPlayer != null) { SetStabbedPlayerClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId); }
                }
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }
        }

        // if setting to null just put in 0 as the playerid
        [ClientRpc]
        public void SetCapturedPlayerClientRpc(ulong playerid, bool reset = false)
        {
            if (reset) { capturedPlayer = null; }

            RoundManager m = RoundManager.Instance;
            var players = m.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (ply.NetworkObject.NetworkObjectId == playerid)
                {
                    capturedPlayer = ply;
                }
            }
        }


        // if setting to null just put in 0 as the playerid
        [ClientRpc]
        public void SetStabbedPlayerClientRpc(ulong playerid, bool reset = false)
        {
            if (reset) { stabbedPlayer = null; }

            RoundManager m = RoundManager.Instance;
            var players = m.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (ply.NetworkObject.NetworkObjectId == playerid)
                {
                    stabbedPlayer = ply;
                }
            }
        }

        [ClientRpc]
        public void DmgPlayerClientRpc(ulong playerid, int amount)
        {
            RoundManager m = RoundManager.Instance;
            var players = m.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (ply.NetworkObject.NetworkObjectId == playerid)
                {
                    ply.DamagePlayer(30);
                }
            }
        }

        public IEnumerator DoSneakyStab()
        {
            if (getNearestPlayer())
            {
                Think("DoSneakyStab");
                DoAnimationClientRpc(7);
                animPlayClientRpc("SneakyStab");
                moaiSoundPlayClientRpc("creatureSneakyStab");

                yield return new WaitForSeconds(0.4f);

                stabbedPlayer = getNearestPlayer();
                if (stabbedPlayer != null) { SetStabbedPlayerClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId); }
                capturedPlayer = null;
                SetCapturedPlayerClientRpc(0, true);
                stabbedCapturedPlayer = true;  // <-- important flag
                DmgPlayerClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId, 30);

                yield return new WaitForSeconds(0.65f);

                doneStab = true;
                Think("Stab Done");
            }
            else  // we need to have a player to sneaky stab!
            {
                // do various things based on the result of the stab
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                isStabbing = false;
                doneStab = false;
                if (!stabbedPlayer)
                {
                    stabbedPlayer = getNearestPlayer();
                    if (stabbedPlayer != null) { SetStabbedPlayerClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId); }
                }
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }
        }

        bool doneStab = false;
        bool isStabbing = false;
        public void baseStabbingCapturedPlayer()
        {
            //Think("basestabbing");
            agent.speed = 0f;
            setAnimationSpeedClientRpc(1);

            if (isEnemyDead)
            {
                agent.updateRotation = true;
                isStabbing = false;
                doneStab = false;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }

            // call routine and wait for it to be done
            if (!isStabbing)
            {
                StartCoroutine(DoStab());
                isStabbing = true;
            }
            else if (doneStab)
            {
                // do various things based on the result of the stab
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                isStabbing = false;
                doneStab = false;
                capturedPlayer = null;
                SetCapturedPlayerClientRpc(0, true);
                if (!stabbedPlayer)
                {
                    stabbedPlayer = getNearestPlayer();
                    if (stabbedPlayer != null) { SetStabbedPlayerClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId); }
                }
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                DoAnimationClientRpc(0);
                animPlayClientRpc("Idle");
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }
        }

        public Transform stabPoint;
        public AudioSource creatureStab;
        public IEnumerator DoStab()
        {
            Think("DoStab");
            DoAnimationClientRpc(5);
            animPlayClientRpc("StabVictimHeld");
            moaiSoundPlayClientRpc("creatureStab");
            if (capturedPlayer)
            {
                SetColliderClientRpc(capturedPlayer.NetworkObject.NetworkObjectId, false);
            }
            yield return new WaitForSeconds(1.1f);

            stabbedPlayer = capturedPlayer;
            if (stabbedPlayer != null) { SetStabbedPlayerClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId); }
            capturedPlayer = null;
            SetCapturedPlayerClientRpc(0, false);
            stabbedCapturedPlayer = true;  // <-- important flag
            DmgPlayerClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId, 30);

            yield return new WaitForSeconds(1.3f);
            if (stabbedPlayer)
            {
                SetColliderClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId, false);
            }

            doneStab = true;
            Think("Stab Done");
        }

        bool doneLeap = false;
        bool isLeaping = false;
        float leapTimer = 9f;
        public void baseLeaping()
        {
            agent.updateRotation = false;
            agent.speed = 0f;
            setAnimationSpeedClientRpc(1);
            targetPlayer = getNearestPlayer();

            leapTimer -= 0.2f;

            if (targetPlayer == null)
            {
                // do various things based on the result of the leap 
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }

            facePosition(targetPlayer.transform.position);
            // call routine and wait for it to be done
            if (!isLeaping && Vector3.Distance(transform.position, targetPlayer.transform.position) <= maxLeapDistance)
            {
                DoGroundHopClientRpc(targetPlayer.transform.position, transform.position);
                isLeaping = true;
            }
            else if (doneLeap)
            {
                // do various things based on the result of the leap 
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }
            else if (leapTimer < 0f)
            {
                // do various things based on the result of the leap 
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }

        }


        float leapAnimationLength = 0.5f;
        float leapPeakHeight = 3f;
        public IEnumerator DoGroundHop(Vector3 targetPos, Vector3 shamblerStartPos)
        {
            Think("DoGroundHop");
            if (RoundManager.Instance.IsHost)
            {
                animPlayClientRpc("Leap-Land");
                moaiSoundPlayClientRpc("creatureLeapLand");
            }

            transform.position = shamblerStartPos;
            Vector3 start = shamblerStartPos;
            // duration of leap gets + 0.5 seconds at max distance
            float duration = leapAnimationLength + (Vector3.Distance(transform.position, targetPos) / maxLeapDistance) * 0.5f;
            float elapsed = 0f;
            float arcPeak = leapPeakHeight;

            while (elapsed < duration)
            {
                float t = elapsed / duration;
                float height = Mathf.Sin(t * Mathf.PI) * arcPeak;
                transform.position = Vector3.Lerp(start, targetPos, t) + Vector3.up * height;
                elapsed += Time.deltaTime;
                yield return null;
            }
            transform.position = targetPos;

            // landing part
            Think("Landing...");
            if (RoundManager.Instance.IsHost)
            {
                animPlayClientRpc("Land-Capture");

                // if the player is within a set area, capture them
                AttemptCapturePlayer();
                DoAnimationClientRpc(4);


                yield return new WaitForSeconds(1.2f);

                doneLeap = true;
            }
            Think("Leap Done");
        }

        // used for hops so clients can truly see where the shambler is during the hop (leaps look very short on client)
        [ClientRpc]
        public void DoGroundHopClientRpc(Vector3 targetPos, Vector3 shamblerStartPos)
        {
            StartCoroutine(DoGroundHop(targetPos, shamblerStartPos));
        }

        // clear out any stabs or captures from other shamblers
        // THIS HUMAN IS MINE!!!
        public void ClaimHuman()
        {

        }


        public List<ulong> EscapingEmployees = new List<ulong>();
        // used by a stake object to tell a shambler someone's trying to escape
        public void StakeNotify(PlayerControllerB player)
        {
            if (player != null)
            {
                EscapingEmployees.Add(player.NetworkObject.NetworkObjectId);
            }
        }

        public void StakeUnNotify(PlayerControllerB player)
        {
            if (player != null)
            {
                EscapingEmployees.Remove(player.NetworkObject.NetworkObjectId);
            }
            else
            {
                EscapingEmployees.Clear();
            }
        }

        public PlayerControllerB capturedPlayer = null;  // currently grabbed player
        public PlayerControllerB stabbedPlayer = null;  // currently stabbed player
        bool stabbedCapturedPlayer = false;
        public Transform capturePoint; // place to scan for grabs, and where to attach to
        public float captureRange = 5.35f;
        public float timeTillStab = 0f;
        public void AttemptCapturePlayer()
        {
            Think("Shambler: Attempt Capture Player");
            var players = RoundManager.Instance.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (Vector3.Distance(ply.transform.position, transform.position) < captureRange)
                {
                    if (ply != capturedPlayer)
                    {
                        Think("Shambler: I GOTCHA");
                        capturedPlayer = ply;
                        stabbedCapturedPlayer = false;
                        attachPlayerClientRpc(capturedPlayer.NetworkObject.NetworkObjectId, false, 50);
                        timeTillStab = (float)(2f + (30f * enemyRandom.NextDouble() * enemyRandom.NextDouble() * enemyRandom.NextDouble()));
                        DmgPlayerClientRpc(ply.NetworkObject.NetworkObjectId, 20);
                        return;
                    }
                    else
                    {
                        DmgPlayerClientRpc(ply.NetworkObject.NetworkObjectId, 70);
                    }
                }
            }
            Think("Shambler: OHHHH I MISSED!");
        }

        float crouchTimer = 0f;
        float maxLeapDistance = 25f;
        public float crouchTimeout = 6f;
        public void baseCrouching()
        {
            agent.speed = 0f;
            setAnimationSpeedClientRpc(1);
            DoAnimationClientRpc(2);
            animPlayClientRpc("Crouching");
            Debug.Log("Crouching... timer: " + crouchTimer);
            crouchTimeout -= 0.2f;

            var ply = (targetPlayer != null && PlayerIsTargetable(targetPlayer)) ? targetPlayer : getNearestPlayer();

            if (Vector3.Distance(transform.position, ply.transform.position) > maxLeapDistance)
            {
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }
            crouchTimer -= 0.2f;

            if (crouchTimer <= 0)
            {
                Think("Switched to leap mode");
                doneLeap = false;
                isLeaping = false;
                leapTimer = 9f;
                SwitchToBehaviourClientRpc((int)State.Leaping);
                return;
            }

            // SUPER JUMP RAAAAAAAAAAAH
            if (crouchTimeout <= 0)
            {
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }
        }

        // --- Cover / spotted control ---
        private float spottedCooldown = 0f; // seconds; prevents yo-yo when backing off
        private LayerMask WorldMask;        // environment-only mask

        // Build a sane environment-only mask based on layer names
        // === Replace BuildWorldMask() with this ===
        private LayerMask BuildWorldMask()
        {
            // Layers considered "static world". Add/remove to match your project.
            string[] include = new string[]
            {
                "Default",              // keep only if your static level uses Default
                "Room",
                "Colliders",
                "MiscLevelGeometry",
                "Terrain",
                "Railing",
                "DecalStickableSurface"
            };

            // Layers to force-exclude (players, enemies, triggers, ragdolls, etc.)
            string[] exclude = new string[]
            {
                "Player",
                "Enemy",
                "Enemies",
                "Players",
                "Ragdoll",
                "Trigger",
                "Ignore Raycast",
                "UI",
            };

            int mask = 0;
            foreach (var n in include)
            {
                int li = LayerMask.NameToLayer(n);
                if (li >= 0) mask |= 1 << li;
                else Debug.LogWarning($"[WorldMask] Include layer \"{n}\" not found.");
            }
            foreach (var n in exclude)
            {
                int li = LayerMask.NameToLayer(n);
                if (li >= 0) mask &= ~(1 << li);
            }

            // Also exclude our own current layer to avoid self-hits.
            mask &= ~(1 << gameObject.layer);

            if (mask == 0)
            {
                Debug.LogWarning("[WorldMask] Mask resolved to 0; falling back to Physics.DefaultRaycastLayers.");
                mask = Physics.DefaultRaycastLayers & ~(1 << gameObject.layer);
            }

            Debug.Log($"[WorldMask] Built mask={mask} (self layer excluded: {LayerMask.LayerToName(gameObject.layer)})");
            return mask;
        }

        // LOS-aware cover quality vs all players (higher is better)
        private float CoverScoreAllPlayers(Vector3 pos)
        {
            var arr = RoundManager.Instance.playersManager.allPlayerScripts;
            int viewers = 0;
            float maxGaze = 0f;

            for (int i = 0; i < arr.Length; i++)
            {
                var p = arr[i];
                if (p == null || !p.isPlayerControlled || p.isPlayerDead) continue;

                bool occluded = OccludedFromPlayer_Multi(pos, p);
                if (!occluded)
                {
                    viewers++;
                    Vector3 toPos = (pos - p.transform.position).normalized;
                    float align = Mathf.Max(0f, Vector3.Dot(p.transform.forward, toPos)); // 0..1
                    if (align > maxGaze) maxGaze = align;
                }
            }

            return (viewers == 0) ? +2.0f : (-0.9f * viewers - 0.8f * maxGaze);
        }

        // Ray from player's eye toward 'around'; step behind hit surface along normal
        private bool TryFindCoverAgainstPlayer(PlayerControllerB ply, Vector3 around, float searchRadius, float backoff, out Vector3 coverPos)
        {
            coverPos = Vector3.zero;
            if (ply == null) return false;

            Vector3 eye = (ply.playerEye != null ? ply.playerEye.transform.position : ply.transform.position + Vector3.up * 1.6f);
            Vector3 dir = (around - eye);
            float maxDist = Mathf.Max(searchRadius, dir.magnitude + searchRadius);

            if (Physics.Raycast(eye, dir.normalized, out RaycastHit hit, maxDist, WorldMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 candidate = hit.point + hit.normal * backoff;

                if (TrySampleNavmesh(candidate, 2.5f, out var navPos) && OccludedFromPlayer_Multi(navPos, ply))
                {
                    coverPos = navPos;
                    return true;
                }
            }
            return false;
        }

        // Radial scan for occluded points around 'center'
        private bool TryFindGroupCover(Vector3 center, float minR, float maxR, int rings, int samplesPerRing, out Vector3 best)
        {
            best = Vector3.zero;
            float bestScore = float.NegativeInfinity;

            for (int r = 0; r < rings; r++)
            {
                float t = (rings == 1 ? 1f : (float)r / (rings - 1));
                float radius = Mathf.Lerp(minR, maxR, t);

                for (int s = 0; s < samplesPerRing; s++)
                {
                    float ang = (s / (float)samplesPerRing) * Mathf.PI * 2f;
                    Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                    Vector3 raw = center + dir * radius;

                    if (!TrySampleNavmesh(raw, 2.5f, out var navPos)) continue;

                    float cover = CoverScoreAllPlayers(navPos);
                    if (cover <= 0.1f) continue; // require some occlusion

                    float ideal = 9.5f; // nice stalking band
                    float dist = Vector3.Distance(navPos, center);
                    float distScore = 1f - Mathf.Clamp01(Mathf.Abs(dist - ideal) / ideal);

                    float myDist = Vector3.Distance(transform.position, navPos);
                    float travelScore = 1f - Mathf.Clamp01(myDist / 25f);

                    float score = 1.4f * cover + 0.8f * distScore + 0.3f * travelScore;

                    if (score > bestScore) { bestScore = score; best = navPos; }
                }
            }

            return bestScore > float.NegativeInfinity;
        }

        // Find the strongest onlooker for "spotted → retreat" rule
        private bool TryGetMostDangerousViewer(out PlayerControllerB danger, out float align)
        {
            danger = null;
            align = 0f;
            var arr = RoundManager.Instance.playersManager.allPlayerScripts;

            for (int i = 0; i < arr.Length; i++)
            {
                var p = arr[i];
                if (p == null || !p.isPlayerControlled || p.isPlayerDead) continue;

                // visible from current position?
                if (!OccludedFromPlayer_Multi(transform.position, p))
                {
                    Vector3 toMe = (transform.position - p.transform.position).normalized;
                    float a = Mathf.Max(0f, Vector3.Dot(p.transform.forward, toMe));
                    if (a > align) { align = a; danger = p; }
                }
            }
            return danger != null;
        }

        private bool ReachedGoal(Vector3 goal)
        {
            if (goal == Vector3.zero) return false;
            return Vector3.Distance(transform.position, goal) <= goalArrivalTol;
        }

        private bool PathBadOrPartial()
        {
            return agent.pathStatus == NavMeshPathStatus.PathPartial || agent.pathStatus == NavMeshPathStatus.PathInvalid;
        }

        private bool CooldownsAllowRepath()
        {
            // time lock + short cooldown to avoid thrash
            if (Time.time - stickyPickedAt < goalMinLockSeconds) return false;
            if (Time.time - stickyPickedAt < goalRepathCooldown) return false;
            return true;
        }

        private void ApplyStickyDestination(Vector3 goal)
        {
            if (goal == Vector3.zero) return;
            if (stickyGoal != goal) agent.SetDestination(goal);
            stickyGoal = goal;
            usingCustomGoal = true;                 // keep base from yanking it back to targetPlayer
            movingTowardsTargetPlayer = false;
        }

        // --- Sticky goal / anti-oscillation ---
        private Vector3 stickyGoal = Vector3.zero;
        private float stickyScore = float.NegativeInfinity;
        private float stickyPickedAt = -999f;
        private float lastGoalDist = 9999f;
        private float stuckTimer = 0f;

        // Tunables
        [SerializeField] private float goalMinLockSeconds = 1.2f;   // keep a goal at least this long
        [SerializeField] private float goalRepathCooldown = 0.6f;    // don't recompute too often
        [SerializeField] private float goalBetterBy = 0.35f;         // newScore must beat oldScore by this
        [SerializeField] private float goalArrivalTol = 0.9f;        // how close counts as "arrived"
        [SerializeField] private float stuckSpeedThresh = 0.15f;     // speed below this → "maybe stuck"
        [SerializeField] private float stuckSeconds = 1.1f;          // time below speed to trigger rethink

        public void DistanceBasedPace(PlayerControllerB ply)
        {
            // go faster if the player is sprinting too...
            if(ply.isSprinting)
            {
                agent.speed = 8.2f * Plugin.moaiGlobalSpeed.Value * Math.Max(1, (16 / Vector3.Distance(transform.position, ply.transform.position) * 0.5f + 1));
            }
            else
            {
                agent.speed = 6f * Plugin.moaiGlobalSpeed.Value * Math.Max(1, (16 / Vector3.Distance(transform.position, ply.transform.position) * 0.5f + 1));
            }
        }

        // picks a Vector to travel to with a NavMeshAgent
        // depending on the alert level, this vector may be really sneaky (<40, avoiding sight as much as possible)
        // completely reckless (40+)
        public Vector3 chooseTravelGoal(out float outScore)
        {
            outScore = float.NegativeInfinity;

            // Prefer current target, else nearest
            var ply = (targetPlayer != null && PlayerIsTargetable(targetPlayer)) ? targetPlayer : getNearestPlayer();
            if (ply == null)
            {
                Think("chooseTravelGoal: no valid player — staying put.");
                return transform.position;
            }
            DistanceBasedPace(ply);  // get faster when closer (counters player sprint speed) 

            if (alertLevel >= sneakyAlertLevel)
            {
                DistanceBasedPace(ply);  // get faster when closer (counters player sprint speed) 
                return getNearestPlayer().transform.position;
            }

            bool sneaking = alertLevel < sneakyAlertLevel;
            Vector3 pPos = ply.transform.position;
            Vector3 pFwd = ply.transform.forward;
            Vector3 pRight = ply.transform.right;

            // --- A) retreat to cover if spotted strongly while sneaking ---
            if (sneaking && spottedCooldown <= 0f && TryGetMostDangerousViewer(out var watcher, out var gazeAlign))
            {
                float distToWatcher = Vector3.Distance(transform.position, watcher.transform.position);
                if (gazeAlign >= 0.6f && distToWatcher <= 20f)
                {
                    Vector3 away = (transform.position - watcher.transform.position); away.y = 0f;
                    Vector3 biasPoint = transform.position + away.normalized * 8f;

                    if (TryFindCoverAgainstPlayer(watcher, biasPoint, 16f, backoff: 1.2f, out var retreat))
                    {
                        spottedCooldown = 3f;
                        outScore = 5f;
                        //Think($"chooseTravelGoal: spotted by {watcher.playerUsername} (align={gazeAlign:F2}) → retreat cover {retreat}");
                        return retreat;
                    }

                    if (TryFindGroupCover(biasPoint, 8f, 16f, 3, 16, out var groupRetreat))
                    {
                        spottedCooldown = 3f;
                        outScore = 4f;
                        //Think($"chooseTravelGoal: spotted → group cover {groupRetreat}");
                        return groupRetreat;
                    }

                    Vector3 brute = transform.position + away.normalized * 6f;
                    if (TrySampleNavmesh(brute, 3f, out var bruteNav))
                    {
                        spottedCooldown = 2f;
                        outScore = 2f;
                        //Think($"chooseTravelGoal: spotted → brute backoff {bruteNav}");
                        return bruteNav;
                    }
                }
            }

            // --- B) seek cover near the target when sneaking ---
            if (sneaking && TryFindCoverAgainstPlayer(ply, pPos, 18f, backoff: 1.1f, out var coverVsTarget))
            {
                outScore = 3.5f;
                //Think($"chooseTravelGoal: cover vs target {coverVsTarget}");
                DistanceBasedPace(ply);  // get faster when closer (counters player sprint speed) 
                return coverVsTarget;
            }
            if (sneaking && TryFindGroupCover(pPos, 1.1f, 14f, 3, 16, out var groupCover))
            {
                outScore = 3.0f;
                //Think($"chooseTravelGoal: group cover {groupCover}");
                DistanceBasedPace(ply);  // get faster when closer (counters player sprint speed) 
                return groupCover;
            }

            // --- C) fallback: flank/behind with LOS folded in ---
            float sneakRadiusNear = 7.5f, sneakRadiusFar = 11f, flankRadius = 9f, closeRadius = 0f, navSampleRange = 6f;

            var candidates = new List<Vector3>(8)
            {
                pPos - pFwd * sneakRadiusNear,
                pPos - pFwd * sneakRadiusFar,
                pPos - pFwd * flankRadius + pRight * (flankRadius * 0.75f),
                pPos - pFwd * flankRadius - pRight * (flankRadius * 0.75f),
                pPos + (Quaternion.AngleAxis(35f, Vector3.up)  * (-pFwd)) * closeRadius,
                pPos + (Quaternion.AngleAxis(-35f, Vector3.up) * (-pFwd)) * closeRadius,
                pPos - pFwd * (closeRadius * 0.6f),
                pPos - pFwd * (closeRadius * 1.3f),
            };
            for (int i = 0; i < candidates.Count; i++)
                candidates[i] += new Vector3(UnityEngine.Random.Range(-0.75f, 0.75f), 0f, UnityEngine.Random.Range(-0.75f, 0.75f));

            float bestScore = float.NegativeInfinity;
            Vector3 bestPos = transform.position;

            foreach (var raw in candidates)
            {
                if (!TrySampleNavmesh(raw, navSampleRange, out var navPos)) continue;

                Vector3 fromPlayer = (navPos - pPos).normalized;
                float behind = Mathf.Clamp01(-Vector3.Dot(pFwd, fromPlayer));

                float ideal = sneaking ? 9.5f : 6.5f;
                float d = Vector3.Distance(navPos, pPos);
                float distScore = 1f - Mathf.Clamp01(Mathf.Abs(d - ideal) / ideal);

                float cover = CoverScoreAllPlayers(navPos);
                float losScore = sneaking ? cover : Mathf.Max(cover, -0.4f); // tolerate some vis when pressing

                bool candVisible = cover <= 0.1f; // your convention: small/neg cover ⇒ visible to someone

                // Hysteresis bias: if recently seen, punish visible goals
                if (Time.time - lastSeenTime < seenGrace && candVisible)
                {
                    // Push this candidate down so we don't immediately walk back into view
                    losScore -= 1.2f; // tweakable
                }

                float myDist = Vector3.Distance(transform.position, navPos);
                float travelScore = 1f - Mathf.Clamp01(myDist / 30f);

                float lateral = Mathf.Abs(Vector3.Dot(fromPlayer, pRight));
                float lateralScore = sneaking ? 0.15f * lateral : 0.35f * lateral;

                float wBehind = sneaking ? 1.0f : 0.6f;
                float score = wBehind * behind + 1.0f * distScore + 1.2f * losScore + 0.3f * travelScore + 0.4f * lateralScore;

                if (score > bestScore) { bestScore = score; bestPos = navPos; }
            }

            if (bestScore == float.NegativeInfinity)
            {
                Vector3 n = pPos - transform.position; n.y = 0f;
                if (n.sqrMagnitude > 0.01f)
                {
                    n = Quaternion.AngleAxis(20f * (UnityEngine.Random.value > 0.5f ? 1f : -1f), Vector3.up) * n.normalized * 4f;
                    if (!TrySampleNavmesh(transform.position + n, 6f, out bestPos))
                        bestPos = transform.position;
                }
            }

            DistanceBasedPace(ply);  // get faster when closer (counters player sprint speed) 
            outScore = bestScore;
            //Think($"🎯 Selected goal at {bestPos} (score={bestScore:F2})");
            return bestPos;
        }

        // Put this inside your ShamblerEnemy class
        private bool TrySampleNavmesh(Vector3 pos, float maxDist, out Vector3 hitPos, int areaMask = NavMesh.AllAreas)
        {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(pos, out hit, maxDist, areaMask))
            {
                hitPos = hit.position;
                return true;
            }
            hitPos = Vector3.zero;
            return false;
        }

        // Get all living, controlled players
        private IEnumerable<PlayerControllerB> AllActivePlayers()
        {
            var arr = RoundManager.Instance.playersManager.allPlayerScripts;
            for (int i = 0; i < arr.Length; i++)
            {
                var p = arr[i];
                if (p != null && p.isPlayerControlled && !p.isPlayerDead) yield return p;
            }
        }

        // Returns true if player has clear LOS to candidatePos (not used by new cover scoring, but kept if needed)
        private bool PlayerHasLOSTo(Vector3 candidatePos, PlayerControllerB ply, LayerMask mask, float raise = 1.4f)
        {
            Vector3 from = ply.playerEye != null ? ply.playerEye.transform.position
                                                 : (ply.transform.position + Vector3.up * 1.6f);
            Vector3 to = candidatePos + Vector3.up * raise;
            return !Physics.Linecast(from, to, out _, mask, QueryTriggerInteraction.Ignore);
        }

        private bool IsVisibleToAnyPlayers(Vector3 pos)
        {
            var arr = RoundManager.Instance.playersManager.allPlayerScripts;
            for (int i = 0; i < arr.Length; i++)
            {
                var p = arr[i];
                if (p == null || !p.isPlayerControlled || p.isPlayerDead) continue;
                if (!OccludedFromPlayer_Multi(pos, p)) return true; // any clear LOS among the 5 rays
            }
            return false;
        }

        private bool IsOccludedFromAllPlayers(Vector3 pos) => !IsVisibleToAnyPlayers(pos);

        // --- Hysteresis / anti-peekaboo ---
        private float coverLockUntil = -999f;     // stay in cover until this time
        private float lastSeenTime = -999f;       // last time any player had LOS to us
        [SerializeField] private float coverDwellSeconds = 5f; // how long to sit tight once occluded
        [SerializeField] private float seenGrace = 0.9f;          // after being seen, avoid visible goals for this long

        // Raycast sample anchors (assign in prefab):
        public Transform footLCol;
        public Transform footRCol;
        public Transform armLCol;
        public Transform armRCol;
        public Transform headCol;
        private Vector3[] BuildPointsForCandidate(Vector3 candidatePos, PlayerControllerB ply)
        {
            // basis from player-eye -> candidate
            Vector3 eye = (ply != null && ply.playerEye != null)
                ? ply.playerEye.transform.position
                : (ply != null ? ply.transform.position + Vector3.up * 1.6f : candidatePos + Vector3.up * 1.6f);

            Vector3 fwd = (candidatePos - eye).normalized;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 up = Vector3.Cross(fwd, right).normalized;

            // local offsets (fallbacks if any are missing)
            Vector3 lFoot = footLCol ? footLCol.localPosition : new Vector3(-0.25f, 0.10f, 0f);
            Vector3 rFoot = footRCol ? footRCol.localPosition : new Vector3(+0.25f, 0.10f, 0f);
            Vector3 lArm = armLCol ? armLCol.localPosition : new Vector3(-0.45f, 1.20f, 0f);
            Vector3 rArm = armRCol ? armRCol.localPosition : new Vector3(+0.45f, 1.20f, 0f);
            Vector3 head = headCol ? headCol.localPosition : new Vector3(0.00f, 1.70f, 0f);

            Vector3 ToWorld(Vector3 lp) => candidatePos + right * lp.x + up * lp.y + fwd * lp.z;

            return new Vector3[]
            {
        ToWorld(lFoot),
        ToWorld(rFoot),
        ToWorld(lArm),
        ToWorld(rArm),
        ToWorld(head),
            };
        }
        // fraction of points that must be blocked to count as "occluded"
        [SerializeField] private float coverRequiredFraction = 0.7f; // 3/5 or 4/5, tweak as you like

        private bool OccludedFromPlayer_Multi(Vector3 candidatePos, PlayerControllerB ply)
        {
            if (ply == null) return true;

            Vector3 eye = (ply.playerEye != null)
                ? ply.playerEye.transform.position
                : ply.transform.position + Vector3.up * 1.6f;

            var targets = BuildPointsForCandidate(candidatePos, ply);

            int blocked = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                if (Physics.Linecast(eye, targets[i], WorldMask, QueryTriggerInteraction.Ignore))
                    blocked++;
            }

            float frac = blocked / (float)targets.Length;
            return frac >= coverRequiredFraction;
        }


        float maxStabDistance = 4f;
        public void baseClosingDistance()
        {
            lookStep();  // update alert
            agent.speed = 6f * Plugin.moaiGlobalSpeed.Value;
            // walking and standing anims
            if (agent.velocity.magnitude > (agent.speed / 4))
            {
                if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(1); }
                setAnimationSpeedClientRpc(agent.velocity.magnitude / walkAnimationCoefficient);
            }
            else if (agent.velocity.magnitude <= (agent.speed / 8))
            {
                if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(0); }
                setAnimationSpeedClientRpc(1);
            }

            // pick valid target
            var ply = (targetPlayer != null && PlayerIsTargetable(targetPlayer)) ? targetPlayer : getNearestPlayer();
            if (ply == null || getNearestPlayer() == null)
            {
                Think("No valid player; switching to Search.");
                usingCustomGoal = false;
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }

            // Visibility bookkeeping at current position
            bool seenNow = IsVisibleToAnyPlayers(transform.position);
            if (seenNow) lastSeenTime = Time.time;


            // prepare to leap if the player sees us and is close
            if (alertLevel >= 70 && Vector3.Distance(getNearestPlayer().transform.position, transform.position) < maxLeapDistance / 1.5)
            {
                SwitchToBehaviourClientRpc((int)State.Crouching);
                crouchTimer = (float)(0.5 + (enemyRandom.NextDouble() * 1.5));
                crouchTimeout = 7f;
                return;
            }

            // stab the player in the back (sneak attack) if alert level is low and we are very close
            if (alertLevel <= 70 && Vector3.Distance(getNearestPlayer().transform.position, transform.position) < maxStabDistance)
            {
                doneStab = false;
                isStabbing = false;
                SwitchToBehaviourClientRpc((int)State.SneakyStab);
                return;
            }

            // If we’re in cover, lock there briefly to avoid peek oscillation
            bool inCoverNow = !seenNow;
            if (inCoverNow && Time.time < coverLockUntil)
            {
                // Keep current sticky goal without recomputing
                usingCustomGoal = true;
                movingTowardsTargetPlayer = false;
                if (stickyGoal != Vector3.zero && agent.destination != stickyGoal) agent.SetDestination(stickyGoal);
                //Think($"CoverLock: holding cover until t={coverLockUntil:F2}");
                return; // skip repath for this tick
            }

            // arrive / stuck detection
            float distNow = (stickyGoal == Vector3.zero) ? 9999f : Vector3.Distance(transform.position, stickyGoal);
            float speed = agent.velocity.magnitude;

            if (speed < stuckSpeedThresh && distNow > goalArrivalTol) stuckTimer += Time.deltaTime; else stuckTimer = 0f;

            bool arrived = ReachedGoal(stickyGoal);
            bool stuck = stuckTimer >= stuckSeconds;
            bool needNew = arrived || stuck || PathBadOrPartial() || stickyGoal == Vector3.zero;

            // we only *consider* a new goal if cooldowns allow OR we must (arrived/stuck/etc.)
            bool consider = needNew || CooldownsAllowRepath();

            if (consider)
            {
                // Compute a candidate + score (your existing chooser)
                float newScore;
                Vector3 candidate = chooseTravelGoal(out newScore);

                // Accept rules:
                // - if we needNew, accept immediately (we’re forced)
                // - else only accept if it’s clearly better by threshold
                bool better = (newScore > stickyScore + goalBetterBy);

                bool candidateIsVisible = IsVisibleToAnyPlayers(candidate);

                // If we were seen very recently, don’t accept a visible goal unless it’s *much* better
                if (candidateIsVisible && (Time.time - lastSeenTime) < seenGrace)
                {
                    better = newScore > stickyScore + (goalBetterBy + 1.0f); // raise the bar
                }

                if (needNew || better)
                {
                    // Decide cover lock using the candidate we’re about to take
                    if (IsOccludedFromAllPlayers(candidate))
                    {
                        coverLockUntil = Time.time + coverDwellSeconds;
                        //Think($"CoverLock armed until {coverLockUntil:F2} (candidate is occluded).");
                    }
                    else
                    {
                        coverLockUntil = -999f;
                    }

                    stickyScore = newScore;
                    stickyPickedAt = Time.time;
                    ApplyStickyDestination(candidate);


                    //Think($"StickyGoal set → {stickyGoal} (score={stickyScore:F2}, arrived={arrived}, stuck={stuck}, pathBad={PathBadOrPartial()})");
                }
                else
                {
                    // Keep current goal; no SetDestination so the agent continues toward it
                    usingCustomGoal = true;
                    movingTowardsTargetPlayer = false;
                    //Think($"Keep goal {stickyGoal} (cur={stickyScore:F2}, cand={newScore:F2})");
                }
            }
            else
            {
                // Just keep pushing current sticky goal
                usingCustomGoal = true;
                movingTowardsTargetPlayer = false;
            }

            // Maintain destination (in case something else cleared it)
            if (stickyGoal != Vector3.zero && agent.destination != stickyGoal) agent.SetDestination(stickyGoal);

            // book-keeping
            lastGoalDist = distNow;

            // stamina & exit conditions
            //stamina -= 3; // net -7/sec vs regen
            if (ply == null || Vector3.Distance(transform.position, ply.transform.position) > 62f || stamina <= 0)
            {
                Think($"Exiting sneak (reason={(ply == null ? "no target" : stamina <= 0 ? "tired" : "too far")})");
                targetPlayer = null;
                usingCustomGoal = false;
                stickyGoal = Vector3.zero;
                stickyScore = float.NegativeInfinity;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            }
        }

        // lookstep: how closely the player(s) are looking at the Shambler (dot product), called every 0.2s
        float alertLevel = 0;
        float alertDecay = 1.5f;     // decays by this value every 0.2 seconds
        float sneakyAlertLevel = 70; // stalk below this level, above it be reckless
        float alertRate = 1f;     // rate multiplier

        public void lookStep()
        {
            var players = RoundManager.Instance.playersManager.allPlayerScripts;
            float maxAlertUpdate = 0;  // the player with the highest alert score updates the alertLevel
            foreach (var ply in players)
            {
                if (ply == null || ply.isPlayerDead || !ply.isPlayerControlled || !PlayerQualifies(ply)) continue;

                // vector of the player's position to the shambler's position
                Vector3 dirVec = (transform.position - ply.transform.position).normalized;

                // player's look vector
                Vector3 playerLookVec = ply.playerEye.transform.forward.normalized;

                // perpendicular=0, opposite=-, directly at=+; scaled by distance clamp
                var localAlertLevel = Vector3.Dot(playerLookVec, dirVec) *
                                      Math.Max(25 - Vector3.Distance(transform.position, ply.transform.position), 0);

                if (localAlertLevel > maxAlertUpdate)
                {
                    maxAlertUpdate = localAlertLevel;
                }
            }
            if (maxAlertUpdate > 0)
            {
                alertLevel += maxAlertUpdate * alertRate;
            }
        }

        public void baseSearchingForPlayer(float lineOfSightRange = 62f)
        {
            agent.speed = 6f * Plugin.moaiGlobalSpeed.Value;
            agent.angularSpeed = 120;

            // executes once every second
            if (sourcecycle % 5 == 0)
            {
                targetPlayer = null;
            }

            if (agent.velocity.magnitude > (agent.speed / 4))
            {
                if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(1); }
                setAnimationSpeedClientRpc(agent.velocity.magnitude / walkAnimationCoefficient);
            }
            else if (agent.velocity.magnitude <= (agent.speed / 8))
            {
                if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(0); }
                setAnimationSpeedClientRpc(1);
            }

            // sound switch
            if (!creatureVoice.isPlaying)
            {
                moaiSoundPlayClientRpc("creatureVoice");
            }

            // entrance state switch
            updateEntranceChance();
            if (this.enemyRandom.NextDouble() < chanceToLocateEntrance && gameObject.transform.localScale.x <= 2.2f && Plugin.canEnterIndoors.Value)
            {
                Think("Switching to HeadingToEntrance.");
                StopSearch(currentSearch);
                SwitchToBehaviourClientRpc((int)State.HeadingToEntrance);
            }

            if ((FoundClosestPlayerInRange(lineOfSightRange, true) && stamina >= 120) || provokePoints > 0)
            {
                Think("Found player for ClosingDistance.");
                StopSearch(currentSearch);
                SwitchToBehaviourClientRpc((int)State.ClosingDistance);
                return;
            }
            if (stamina < 100)
            {
                targetPlayer = null;
            }

            // transition to heading to nest if a player is currently stabbed
            if (stabbedCapturedPlayer)
            {
                capturedPlayer = null;
                headingToNestDistance = 9999f;
                headingToNestTimeout = 6f;
                SwitchToBehaviourClientRpc((int)State.HeadingToNest);
                StopSearch(currentSearch);
                return;
            }
        }

        // if the headingToNestTimeout doesn't improve after 3 seconds, then plant
        float headingToNestTimeout = 6;
        float headingToNestDistance = 0f;
        public void baseHeadingToNest()
        {
            agent.speed = 6f * Plugin.moaiGlobalSpeed.Value;
            agent.angularSpeed = 120;

            headingToNestTimeout -= 0.2f;
            if (headingToNestTimeout < 0)
            {
                if (headingToNestDistance > Vector3.Distance(transform.position, nestSpot))
                {
                    isPlanting = false;
                    donePlant = false;
                    SwitchToBehaviourClientRpc((int)State.PlantingStake);
                }
                else
                {
                    headingToNestTimeout = 6f;
                    headingToNestDistance = Vector3.Distance(transform.position, nestSpot);
                }
            }

            if (agent.velocity.magnitude > (agent.speed / 4))
            {
                if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(1); }
                setAnimationSpeedClientRpc(agent.velocity.magnitude / walkAnimationCoefficient);
            }
            else if (agent.velocity.magnitude <= (agent.speed / 8))
            {
                if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(0); }
                setAnimationSpeedClientRpc(1);
            }

            SetDestinationToPosition(nestSpot);

            if (Vector3.Distance(transform.position, nestSpot) < 15f)
            {
                isPlanting = false;
                donePlant = false;
                SwitchToBehaviourClientRpc((int)State.PlantingStake);
            }

            if (!stabbedCapturedPlayer)
            {
                isStabbing = false;
                doneStab = false;
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            }

            if (!isOutside)
            {
                SwitchToBehaviourClientRpc((int)State.HeadingToEntrance);
            }
        }

        bool isPlanting = false;
        bool donePlant = false;
        public void basePlantingStake()
        {

            //Think("baseplanting");
            agent.speed = 0f;
            setAnimationSpeedClientRpc(1);

            if (isEnemyDead)
            {
                agent.updateRotation = true;
                isStabbing = false;
                doneStab = false;
                isPlanting = false;
                donePlant = false;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }

            // call routine and wait for it to be done
            if (!isPlanting)
            {
                StartCoroutine(DoPlant());
                isPlanting = true;
            }
            else if (donePlant)
            {
                // do various things based on the result of the stab
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                isPlanting = false;
                donePlant = false;
                animPlayClientRpc("Idle");
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
            }
        }


        public GameObject heldStakeRef;
        public float plantCooldown = 5.0f;  // just to prevent multiple stabs
        public float plantTime = -1;
        public IEnumerator DoPlant()
        {

            if (plantTime + plantCooldown > Time.time)
            {
                // do various things based on the result of the stab
                // (for now its just going back to patrol state
                agent.updateRotation = true;
                isPlanting = false;
                donePlant = false;
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                StartSearch(transform.position);
                Think("Switched to searching for player");
                yield break;
            }
            //Think("DoPlant");
            DoAnimationClientRpc(6);
            animPlayClientRpc("PlantStake");
            moaiSoundPlayClientRpc("creaturePlant");
            plantTime = Time.time;
            if (stabbedPlayer)
            {
                SetColliderClientRpc(stabbedPlayer.NetworkObject.NetworkObjectId, false);
            }

            yield return new WaitForSeconds(1.05f); // moment stake hits ground

            // Determine the victim BEFORE we null anything
            var victim = capturedPlayer != null ? capturedPlayer : stabbedPlayer;
            try
            {
                // Spawn stake on host only
                if (RoundManager.Instance.IsHost)
                {
                    var stakePrefab = Plugin.ShamblerStakePrefab;
                    // Use the held reference to place
                    GameObject stake = Instantiate(stakePrefab, heldStakeRef.transform.position, heldStakeRef.transform.rotation);
                    var obj = stake.GetComponent<ShamblerStake>();
                    var netObj = stake.GetComponent<NetworkObject>();

                    obj.owner = this;
                    obj.victim = victim;  // prefer the actual held/stabbed player, not nearest
                    obj.SetVictimClientRpc(victim.NetworkObject.NetworkObjectId);

                    if (!netObj.IsSpawned) netObj.Spawn();

                    // add the player to the planted list
                    stuckPlayerIds.Add(victim.NetworkObject.NetworkObjectId);
                }

                // Release the player (only if we had one)
                if (victim != null)
                {
                    letGoOfPlayerClientRpc(victim.NetworkObject.NetworkObjectId);
                }

                // Now clear local references / flags
                if (stabbedPlayer != null) { SetStabbedPlayerClientRpc(0, true); }
                if (capturedPlayer != null) { SetCapturedPlayerClientRpc(0, true); }
                stabbedPlayer = null;
                capturedPlayer = null;
                stabbedCapturedPlayer = false;

                // clear out the player
                targetPlayer = null;
                mostRecentPlayer = null;
                usingCustomGoal = false;
                stickyGoal = Vector3.zero;
                stickyScore = float.NegativeInfinity;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Shambler] DoPlant exception: {ex}");
                // Fail-safe: allow state machine to recover instead of perma-lock
            }

            // Let the animation finish
            yield return new WaitForSeconds(0.5f);

            // IMPORTANT: signal completion ONCE and DO NOT reset it here
            donePlant = true;
            isPlanting = false;
            Think("Plant Done");

            yield return new WaitForSeconds(1f);
            SetColliderClientRpc(victim.NetworkObject.NetworkObjectId, true);
        }

        public void baseHeadingToEntrance()
        {
            targetPlayer = null;

            if (agent.velocity.magnitude > (agent.speed / 4))
            {
                if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(1); }
                setAnimationSpeedClientRpc(agent.velocity.magnitude / walkAnimationCoefficient);
            }
            else if (agent.velocity.magnitude <= (agent.speed / 8))
            {
                if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(0); }
                setAnimationSpeedClientRpc(1);
            }

            SetDestinationToPosition(nearestEntranceNavPosition);
            if (this.isOutside != nearestEntrance.isEntranceToBuilding || this.agent.pathStatus == NavMeshPathStatus.PathPartial)
            {
                entranceDelay = 150;
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            }
            if (Vector3.Distance(transform.position, nearestEntranceNavPosition) < (2.0 + gameObject.transform.localScale.x))
            {
                if (nearestEntrance.isEntranceToBuilding)
                {
                    Debug.Log("SHAMBLER: Warp in");
                    EntityWarp.SendEnemyInside(this);
                    nearestEntrance.PlayAudioAtTeleportPositions();
                }
                else
                {
                    Debug.Log("SHAMBLER: Warp out");
                    EntityWarp.SendEnemyOutside(this, true);
                    nearestEntrance.PlayAudioAtTeleportPositions();
                }
                entranceDelay = 150;
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            }

            if (provokePoints > 0)
            {
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            }
        }

        public void updateEntranceChance()
        {
            if (!nearestEntrance) return;

            var dist = Vector3.Distance(transform.position, nearestEntrance.transform.position);

            chanceToLocateEntrancePlayerBonus = 1;
            if (mostRecentPlayer)
            {
                if (mostRecentPlayer.isInsideFactory == this.isOutside) { chanceToLocateEntrancePlayerBonus = 1; }
                else { chanceToLocateEntrancePlayerBonus = 1.5f; }
            }
            var m1 = 1;
            if (dist < 20) { m1 = 4; }
            if (dist < 15) { m1 = 6; }
            if (dist < 10) { m1 = 7; }
            if (dist < 5) { m1 = 10; }

            if (nearestEntrance)
            {
                chanceToLocateEntrance = (float)(1 / Math.Pow(dist, 2)) * m1 * chanceToLocateEntrancePlayerBonus - entranceDelay;
            }
        }

        public bool FoundClosestPlayerInRange(float r, bool needLineOfSight)
        {
            if (recovering) { return false; }
            moaiTargetClosestPlayer(range: r, requireLineOfSight: needLineOfSight);
            return PlayerQualifies(targetPlayer);
        }

        public bool moaiTargetClosestPlayer(float range, bool requireLineOfSight)
        {
            if (recovering) { return false; }

            mostOptimalDistance = range;
            PlayerControllerB playerControllerB = targetPlayer;
            targetPlayer = null;
            for (int i = 0; i < StartOfRound.Instance.connectedPlayersAmount + 1; i++)
            {
                if (PlayerIsTargetable(StartOfRound.Instance.allPlayerScripts[i]) &&
                    (!requireLineOfSight || CheckLineOfSightForPosition(StartOfRound.Instance.allPlayerScripts[i].gameplayCamera.transform.position, Plugin.LOSWidth.Value, 80)))
                {
                    tempDist = Vector3.Distance(base.transform.position, StartOfRound.Instance.allPlayerScripts[i].transform.position);
                    if (tempDist < mostOptimalDistance)
                    {
                        mostOptimalDistance = tempDist;

                        if (RageTarget != null && PlayerQualifies(StartOfRound.Instance.allPlayerScripts[i]) && RageTarget == StartOfRound.Instance.allPlayerScripts[i])
                        {
                            targetPlayer = StartOfRound.Instance.allPlayerScripts[i];
                            return true;
                        }
                        else if (PlayerQualifies(StartOfRound.Instance.allPlayerScripts[i]))
                        {
                            targetPlayer = StartOfRound.Instance.allPlayerScripts[i];
                        }
                    }
                }
            }

            if (targetPlayer != null && playerControllerB != null)
            {
                targetPlayer = playerControllerB;
            }

            return PlayerQualifies(targetPlayer);
        }

        // AI must go through all these checks before retrieving a player to interact with
        PlayerControllerB RageTarget = null;  // if a ragetarget exists things get REALLY BAD
        public bool PlayerQualifies(PlayerControllerB ply)
        {
            if (ply == null || ply.NetworkObject == null) return false;
            if (ply.isInHangarShipRoom) { return false; }  // no ship BS

            // TODO: define a radius around the ship, of which the shambler can not go past

            // If player is escaping AND inside our forward cone (or very close) AND we have LOS, enrage
            if (EscapingEmployees.Contains(ply.NetworkObject.NetworkObjectId))
            {
                Vector3 enemyEye = headCol ? headCol.position : transform.position + Vector3.up * 1.6f;
                Vector3 playerEye = ply.playerEye ? ply.playerEye.transform.position : ply.transform.position + Vector3.up * 1.6f;

                // raise both a bit to avoid ground/obstacle clipping
                enemyEye.y += 0.6f;
                playerEye.y += 0.6f;

                // Flat-forward FOV test with close-range override
                Vector3 toP = ply.transform.position - transform.position;
                toP.y = 0f; // ignore vertical for FOV
                float dist = toP.magnitude;

                // tweak these two as you like
                const float halfFovDeg = 70f;   // how “in front” they must be
                const float closeOverrideM = 8f;    // within this dist, FOV requirement is waived

                bool closeOverride = dist <= closeOverrideM;
                bool fovOk = closeOverride ||
                             (toP.sqrMagnitude > 0.001f &&
                              Vector3.Dot(transform.forward, toP.normalized) >= Mathf.Cos(halfFovDeg * Mathf.Deg2Rad));

                // Clean enemy→player LOS using your world mask
                bool hasLOS = !Physics.Linecast(enemyEye, playerEye, WorldMask, QueryTriggerInteraction.Ignore);

                //if (Physics.Linecast(enemyEye, playerEye, out var hit, WorldMask, QueryTriggerInteraction.Ignore))
                //    Think($"LOS blocked by {hit.collider.name}");

                if (fovOk && hasLOS)
                {
                    alertLevel = 100f;          // if you want rage to also pump alert; otherwise remove this line
                    RageTarget = ply;
                    Think($"⚠ Rage: escaping {ply.playerUsername} dist={dist:F1}m FOV={fovOk} LOS={hasLOS}");
                    ClearRageTarget();          // your cooldown
                    return true;
                }
            }

            if (ply == RageTarget) { return true; }

            // Normal qualification rules
            if (ply == capturedPlayer || ply == stabbedPlayer) return false;
            if (ply == capturedPlayer || ply == stabbedPlayer) return false;
            if (IsPlayerStaked(ply)) return false;
            if (ply.isPlayerDead || !ply.isPlayerControlled) return false;

            return true;
        }

        public async void ClearRageTarget()
        {
            // 10 seconds for the shambler to calm down
            await Task.Delay(10000);
            RageTarget = null;
            EscapingEmployees.Clear();
        }

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            base.HitEnemy(force, playerWhoHit, playHitSFX);
            if (this.isEnemyDead) return;

            if (hitID == -1 && playerWhoHit == null) return;

            // for now, ignore the hit if the player is captured or stabbed
            if (playerWhoHit != null)
            {
                ulong pid = playerWhoHit.NetworkObject.NetworkObjectId;
                if (stabbedPlayer != null && pid == stabbedPlayer.NetworkObject.NetworkObjectId) { return; }
                if (capturedPlayer != null && pid == capturedPlayer.NetworkObject.NetworkObjectId) { return; }
            }

            this.enemyHP -= force;

            if (playerWhoHit != null)
            {
                provokePoints += 20 * force;
                targetPlayer = playerWhoHit;
            }
            stamina = 120;
            recovering = false;

            if (base.IsOwner)
            {
                if (this.enemyHP <= 0 && !markDead)
                {
                    base.KillEnemyOnOwnerClient(false);
                    this.stopAllSound();
                    isEnemyDead = true;
                    moaiSoundPlayClientRpc("creatureDeath");
                    deadEventClientRpc();
                    markDead = true;
                    return;
                }
                moaiSoundPlayClientRpc("creatureHit");
            }
        }

        [ClientRpc]
        public void deadEventClientRpc()
        {
            animator.Play("Death");
            if (!creatureDeath.isPlaying)
            {
                creatureDeath.Play();
            }
            isEnemyDead = true;
        }

        [ServerRpc(RequireOwnership = false)]
        public void letGoOfPlayerServerRpc(ulong playerId) => letGoOfPlayerClientRpc(playerId);

        [ClientRpc]
        public void letGoOfPlayerClientRpc(ulong playerId)
        {
            PlayerControllerB targetPlayer = null;
            RoundManager m = RoundManager.Instance;
            PlayerControllerB[] players = m.playersManager.allPlayerScripts;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player.NetworkObject.NetworkObjectId == playerId) targetPlayer = player;
            }
            //targetPlayer.playerCollider.enabled = true;

            // put the player on a navmesh spot
            if (UnityEngine.AI.NavMesh.SamplePosition(targetPlayer.transform.position, out var hit, 15f, NavMesh.AllAreas))
            {
                targetPlayer.transform.position = hit.position;
            }
            if (stabbedPlayer != null) { SetStabbedPlayerClientRpc(0, true); }
            if (capturedPlayer != null) { SetCapturedPlayerClientRpc(0, true); }
            capturedPlayer = null;
            stabbedPlayer = null;
        }

        public override void OnCollideWithEnemy(Collider other, EnemyAI collidedEnemy = null)
        {
            if (this.timeSinceHittingLocalPlayer < 0.5f || collidedEnemy.isEnemyDead || isEnemyDead) return;
            if (collidedEnemy.enemyType == this.enemyType) return;

            var nam = collidedEnemy.enemyType.name.ToLower();
            if (nam.Contains("mouth") && nam.Contains("dog")) return;
            if (collidedEnemy.enemyType.enemyName.ToLower().Contains("soul")) return;

            this.timeSinceHittingLocalPlayer = 0f;
            collidedEnemy.HitEnemy(1, null, true);
        }

        public override void OnCollideWithPlayer(Collider other)
        {
            if (timeSinceHittingLocalPlayer < 0.6f) return;
            PlayerControllerB pcb = MeetsStandardPlayerCollisionConditions(other);
            if (pcb) DmgPlayerClientRpc(pcb.NetworkObject.NetworkObjectId, 60); ;
        }

        [ServerRpc(RequireOwnership = false)]
        public void attachPlayerServerRpc(ulong uid, bool lastHit, int staminaGrant) => attachPlayerClientRpc(uid, lastHit, staminaGrant);

        [ClientRpc]
        public void attachPlayerClientRpc(ulong uid, bool lastHit, int staminaGrant)
        {
            stamina += staminaGrant;

            RoundManager m = RoundManager.Instance;
            PlayerControllerB[] players = m.playersManager.allPlayerScripts;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player.NetworkObject.NetworkObjectId == uid)
                {
                    if (!lastHit)
                    {
                        player.transform.position = capturePoint.position;
                        //player.playerCollider.enabled = false;
                        capturedPlayer = player;
                    }
                    else
                    {
                        player.deadBody.transform.position = capturePoint.position;
                        capturedPlayer = player;
                    }
                    return;
                }
            }
        }

        [ClientRpc]
        public void attachPlayerSpikeClientRpc(ulong uid, bool lastHit, int staminaGrant)
        {
            stamina += staminaGrant;

            RoundManager m = RoundManager.Instance;
            PlayerControllerB[] players = m.playersManager.allPlayerScripts;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player.NetworkObject.NetworkObjectId == uid)
                {
                    if (!lastHit)
                    {
                        player.transform.position = capturePoint.position;
                        //player.playerCollider.enabled = false;
                        capturedPlayer = player;
                        if (capturedPlayer != null) { SetCapturedPlayerClientRpc(capturedPlayer.NetworkObject.NetworkObjectId); }
                    }
                    else
                    {
                        player.deadBody.transform.position = capturePoint.position;
                        capturedPlayer = player;
                        if (capturedPlayer != null) { SetCapturedPlayerClientRpc(capturedPlayer.NetworkObject.NetworkObjectId); }
                    }
                    return;
                }
            }
        }

        [ClientRpc]
        public void SetColliderClientRpc(ulong playerid, bool value)
        {
            RoundManager m = RoundManager.Instance;
            var players = m.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (ply.NetworkObject.NetworkObjectId == playerid)
                {
                    //ply.playerCollider.enabled = value;
                }
            }
        }

        // sound helpers
        public virtual void playSoundId(String id) { }

        public void stopAllSound()
        {
            creatureSFX.Stop();
            creatureVoice.Stop();
            creatureAnger.Stop();
            creatureLaugh.Stop();
            creatureLeapLand.Stop();
            //creatureSneakyStab.Stop();
            //creaturePlant.Stop();
            //creatureStab.Stop();
        }

        [ClientRpc]
        public void moaiSoundPlayClientRpc(String soundName)
        {
            switch (soundName)
            {
                case "creatureSFX":
                    stopAllSound();
                    creatureSFX.Play();
                    break;
                case "creatureVoice":
                    stopAllSound();
                    double[] timeIntervals = { 0.0, 0.8244, 11.564, 29.11, 34.491, 37.840, 48.689, 64.518, 89.535, 92.111 };
                    int selectedTime = UnityEngine.Random.Range(0, timeIntervals.Length);
                    creatureVoice.Play();
                    creatureVoice.SetScheduledStartTime(timeIntervals[selectedTime]);
                    creatureVoice.time = (float)timeIntervals[selectedTime];
                    break;
                case "creatureLeapLand":
                    stopAllSound();
                    creatureLeapLand.Play();
                    break;
                case "creatureLaugh":
                    stopAllSound();
                    creatureLaugh.Play();
                    break;
                case "creatureAnger":
                    stopAllSound();
                    creatureAnger.Play();
                    break;
                case "creaturePlant":
                    creaturePlant.Play();
                    break;
                case "creatureSneakyStab":
                    stopAllSound();
                    creatureSneakyStab.Play();
                    break;
                case "creatureStab":
                    stopAllSound();
                    creatureStab.Play();
                    break;
                case "creatureDeath":
                    if (!creatureDeath.isPlaying)
                    {
                        stopAllSound();
                        creatureDeath.Play();
                    }
                    break;
                case "creatureHit":
                    creatureTakeDmg.Play();
                    break;
                case "step":
                    if (creatureSteps.Length > 0)
                    {
                        int selectedIndex = enemyRandom.Next(0, creatureSteps.Length);
                        creatureSteps[selectedIndex].Play();
                    }
                    break;
            }
        }

        [ClientRpc]
        public void setAnimationSpeedClientRpc(float speed) => this.animator.speed = speed;

        [ClientRpc]
        // note that this is only for rotation animations (cause its moai)
        // these are synced through a network transform
        public void DoAnimationClientRpc(int index)
        {
            if (this.animator) { this.animator.SetInteger("state", index); }
        }

        [ClientRpc]
        public void animPlayClientRpc(String name) => animator.Play(name);
    }
}