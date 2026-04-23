using GameNetcodeStuff;
using SoulDev;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
namespace Shambler.src.Soul_Devourer
{
    internal class ShamblerStake : NetworkBehaviour
    {
        // set by the shambler
        public ShamblerEnemy owner;
        public PlayerControllerB victim;
        public Transform stabPoint;
        private InteractTrigger envTrigger;
        public AudioSource failEscapeSource;
        public AudioSource successSource;


        // list is managed to ensure that a player can't get attached to 2 stakes at once, ever
        public static List<PlayerControllerB> capturedPlayers = new List<PlayerControllerB>();
        public float damageTimer = 20f; // how often the spike deals dmg
        int freeChance = -1;  // each time someone tries to free themselves, the chance of freedom increases
        int dmgPunishment = 10;  // punishment for failing a free attempt
        int failBoost = 20;  // boost to win chance on failure
        // the shambler will be VERY UNHAPPY if he sees you trying this
        // (hint, he fucking kills you)
        bool IsFreeing = false;
        public static float commonOffset = 1.15f;
        public void Start()
        {
            var plylocal = RoundManager.Instance.playersManager.localPlayerController;
            envTrigger = GetComponent<InteractTrigger>();
            freeChance = Plugin.stakeFreeChance.Value;
            if (RoundManager.Instance.IsHost)
            {
                StartSetupClientRpc();
                NavMeshHit Hit;
                NavMesh.SamplePosition(this.transform.position, out Hit, 10, NavMesh.AllAreas);
                transform.position = new Vector3(Hit.position.x, Hit.position.y + commonOffset, Hit.position.z);
                SetPositionClientRpc(transform.position);
            }
        }
        [ClientRpc]
        public void SetPositionClientRpc(Vector3 pos)
        {
            transform.position = pos;
        }
        public PlayerControllerB NearestPlayer()
        {
            RoundManager m = RoundManager.Instance;
            float lowestDist = 999999f;
            var players = m.playersManager.allPlayerScripts;
            if (players == null || players.Length == 0) return null;
            PlayerControllerB nearestPlayer = players[0];
            foreach (var ply in players)
            {
                if (Vector3.Distance(transform.position, ply.transform.position) < lowestDist)
                {
                    nearestPlayer = ply;
                    lowestDist = Vector3.Distance(transform.position, ply.transform.position);
                }
            }
            return nearestPlayer;
        }
        [ClientRpc]
        public void StartSetupClientRpc()
        {
            if (victim == null) { victim = NearestPlayer(); }

            if (RoundManager.Instance.IsHost)
            {
                try
                {
                    var stakes = FindObjectsOfType<ShamblerStake>();

                    // free player from any other stakes they may be attached to
                    foreach (var stake in stakes)
                    {
                        if (stake.victim.NetworkObjectId == victim.NetworkObjectId && stake.NetworkObjectId != NetworkObjectId)
                        {
                            stake.freeChance = 100;
                            stake.AttemptFree(victim);
                        }
                    }
                    capturedPlayers.Add(victim);
                }
                catch(Exception e) { Debug.LogError(e); }
            }

            var plylocal = RoundManager.Instance.playersManager.localPlayerController;
            envTrigger = GetComponent<InteractTrigger>();
            if (victim != null && victim.NetworkObject.NetworkObjectId == plylocal.NetworkObject.NetworkObjectId)
            {
                envTrigger.hoverTip = "Attempt to Escape (" + freeChance + " % chance) Don't let the shambler notice!";
            }
            else
            {
                envTrigger.hoverTip = "Free Player (100 % chance) Don't let the shambler notice!";
            }
        }
        /// <summary>
        /// Moves the GameObject so its visual center (Renderer bounds center) is placed at the target position.
        /// </summary>
        public static void MoveByCenter(GameObject obj, Vector3 targetPosition)
        {
            if (obj == null) return;
            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            // Calculate combined bounds center
            Bounds combinedBounds = renderers[0].bounds;
            foreach (Renderer r in renderers)
                combinedBounds.Encapsulate(r.bounds);
            // Calculate offset between pivot and center
            Vector3 offset = obj.transform.position - combinedBounds.center;
            // Move so the center aligns with the target position
            obj.transform.position = targetPosition + offset;
        }
        [ClientRpc]
        public void SetVictimClientRpc(ulong playerid)
        {
            RoundManager m = RoundManager.Instance;
            var plylocal = RoundManager.Instance.playersManager.localPlayerController;
            var players = m.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (ply.NetworkObject.NetworkObjectId == playerid)
                {
                    victim = ply;
                    if (victim.NetworkObject.NetworkObjectId == plylocal.NetworkObject.NetworkObjectId)
                    {
                        envTrigger.hoverTip = "Attempt to Escape (" + freeChance + " % chance) Don't let the shambler notice!";
                    }
                    else
                    {
                        envTrigger.hoverTip = "Free Player (100 % chance) Don't let the shambler notice!";
                    }
                }
            }
        }
        float checkCooldown = 0.25f;
        public void Update()
        {
            checkCooldown -= Time.deltaTime;
            if (victim)
            {
                victim.fallValue = 0;
                victim.fallValueUncapped = 0;
            }
            // owner checks for rage mode while a player is trying to free themselves
            if (RoundManager.Instance.IsHost && checkCooldown <= 0 && victim == null && IsFreeing)
            {
                checkCooldown = 0;
                if (owner != null)
                {
                    owner.PlayerQualifies(victim);
                }
            }
            // ensure no fall damage bug bs
            if (victim)
            {
                victim.fallValue = 0;
                victim.fallValueUncapped = 0;
            }
            if (victim)
            {
                victim.transform.position = stabPoint.position;
                if (victim.playerCollider.enabled)
                {
                    //SetColliderClientRpc(victim.NetworkObject.NetworkObjectId, false);
                }
            }
            if (RoundManager.Instance.IsHost)
            {
                // just to make things more forgiving
                if (victim != null && !envTrigger.isBeingHeldByPlayer && owner != null && owner.EscapingEmployees.Contains(victim.NetworkObject.NetworkObjectId))
                {
                    IsFreeing = false;
                    owner.StakeUnNotify(victim);
                }
                // disable self on recapture (avoiding a duplicate stake planting bug)
                if (victim != null)
                {
                    if ((owner != null && owner.capturedPlayer == victim) || victim.isPlayerDead)
                    {
                        if (RoundManager.Instance.IsHost)
                        {
                            DelayedUnNotifLong(victim);
                            SetColliderClientRpc(victim.NetworkObject.NetworkObjectId, true);
                            ShamblerEnemy.stuckPlayerIds.Remove(victim.NetworkObject.NetworkObjectId);
                            ResetFallValuesClientRpc(victim.NetworkObject.NetworkObjectId);
                            DetachClientRpc();
                            PlaySuccessClientRpc();
                            try
                            {
                                DisableInteractClientRpc();
                            }
                            catch (Exception e)
                            {
                                Debug.Log("Shambler Stake Error: " + e.ToString());
                            }
                        }
                    }
                }
            }
        }
        [ClientRpc]
        public void DetachClientRpc()
        {
            if (victim != null)
            {
                if (ShamblerEnemy.stuckPlayerIds != null)
                {
                    ShamblerEnemy.stuckPlayerIds.Remove(victim.NetworkObject.NetworkObjectId);
                }
                //victim.playerCollider.enabled = true;
            }
            victim = null;
        }
        // client and server call this
        public void AttemptFree(PlayerControllerB caller)
        {
            Debug.Log("stake attempt free:");
            if (!RoundManager.Instance.IsHost)
            {
                AttemptFreeServerRpc(caller.NetworkObject.NetworkObjectId);
                return;
            }
            // null guard: if victim is gone there's nothing to free
            if (victim == null) return;
            if (victim.isPlayerDead) { DetachClientRpc(); }
            if (owner != null)
            {
                owner.StakeNotify(victim);
            }
            IsFreeing = false;
            if (UnityEngine.Random.RandomRangeInt(0, 100) < freeChance || (caller.NetworkObject.NetworkObjectId != victim.NetworkObject.NetworkObjectId))
            {
                if (victim)
                {
                    SnapToNavmesh(victim); // prevent the falling through world bug
                    DelayedUnNotifLong(victim);
                    //victim.playerCollider.enabled = true;
                    ShamblerEnemy.stuckPlayerIds.Remove(victim.NetworkObject.NetworkObjectId);
                    ResetFallValuesClientRpc(victim.NetworkObject.NetworkObjectId);
                    DetachClientRpc();
                    PlaySuccessClientRpc();
                    try
                    {
                        DisableInteractClientRpc();
                    }
                    catch (Exception e)
                    {
                        Debug.Log("Shambler Stake Error: " + e.ToString());
                    }
                }
            }
            else
            {
                if (victim)
                {
                    DmgPlayerClientRpc(victim.NetworkObject.NetworkObjectId, dmgPunishment);
                    PlayFailEscapeClientRpc();
                    updateStatsClientRpc(5, failBoost);
                    if (owner != null)
                    {
                        owner.StakeUnNotify(victim);
                    }
                }
            }
        }
        // prevents player from falling off the map when freeing themselves
        public void SnapToNavmesh(PlayerControllerB ply)
        {
            NavMeshHit hit;
            bool sample = NavMesh.SamplePosition(ply.transform.position, out hit, 10f, NavMesh.AllAreas);
            if (sample)
            {
                NavSnapClientRpc(ply.NetworkObject.NetworkObjectId, hit.position);
                Debug.Log("Snapped to position: " + hit.position);
            }
        }
        [ClientRpc]
        public void NavSnapClientRpc(ulong playerid, Vector3 pos)
        {
            RoundManager m = RoundManager.Instance;
            var plylocal = RoundManager.Instance.playersManager.localPlayerController;
            var players = m.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (ply.NetworkObject.NetworkObjectId == playerid)
                {
                    ply.transform.position = pos;
                }
            }
        }
        [ClientRpc]
        public void updateStatsClientRpc(int dmgPunishmentChange, int freeChanceChange)
        {
            var plylocal = RoundManager.Instance.playersManager.localPlayerController;
            dmgPunishment += dmgPunishmentChange;
            freeChance += freeChanceChange;
            if (victim != null && victim.NetworkObject.NetworkObjectId == plylocal.NetworkObject.NetworkObjectId)
            {
                envTrigger.hoverTip = "Attempt to Escape (" + freeChance + " % chance) Don't let the shambler notice!";
            }
            else
            {
                envTrigger.hoverTip = "Free Player (100 % chance) Don't let the shambler notice!";
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
                    ply.DamagePlayer(Plugin.stakeFailDmg.Value, true, true, CauseOfDeath.Stabbing, 0, false, default(Vector3));
                }
            }
        }
        [ClientRpc]
        public void SetHoverTipClientRpc(string tip)
        {
            GetComponent<InteractTrigger>().hoverTip = tip;
        }
        [ClientRpc]
        public void DisableInteractClientRpc()
        {
            GetComponent<InteractTrigger>().enabled = false;
        }
        [ClientRpc]
        public void ResetFallValuesClientRpc(ulong playerid)
        {
            RoundManager m = RoundManager.Instance;
            var players = m.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (ply.NetworkObject.NetworkObjectId == playerid)
                {
                    ply.fallValue = 0;
                    ply.fallValueUncapped = 0;
                    ply.playerRigidbody.velocity = Vector3.zero;
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
        [ServerRpc(RequireOwnership = false)]
        public void AttemptFreeServerRpc(ulong playerid)
        {
            Debug.Log("stake attempt free (SERVERRPC):");
            RoundManager m = RoundManager.Instance;
            var players = m.playersManager.allPlayerScripts;
            foreach (var ply in players)
            {
                if (ply.NetworkObject.NetworkObjectId == playerid)
                {
                    AttemptFree(ply);
                }
            }
        }
        [ClientRpc]
        public void PlaySuccessClientRpc()
        {
            successSource.Play();
        }
        [ClientRpc]
        public void PlayFailEscapeClientRpc()
        {
            failEscapeSource.Play();
        }
        public async void DelayedUnNotifLong(PlayerControllerB ply)
        {
            await Task.Delay(9000);
            if (owner != null)
            {
                owner.StakeUnNotify(ply);
            }
        }
        public void StartInteract()
        {
            if (RoundManager.Instance.IsHost)
            {
                IsFreeing = true;
                if (owner != null)
                {
                    owner.StakeNotify(victim);
                }
            }
        }
        public void StopInteract()
        {
            if (RoundManager.Instance.IsHost)
            {
                IsFreeing = false;
                if (owner != null)
                {
                    owner.StakeUnNotify(victim);
                }
            }
        }
        public void OnDestroy()
        {
            if (victim)
            {
                if (RoundManager.Instance.IsHost)
                {
                    if (victim)
                    {
                        SetColliderClientRpc(victim.NetworkObject.NetworkObjectId, true);
                        //victim.playerCollider.enabled = true;
                    }
                    ShamblerEnemy.stuckPlayerIds.Remove(victim.NetworkObject.NetworkObjectId);
                }
                else
                {
                    if (victim)
                    {
                        //victim.playerCollider.enabled = true;
                    }
                }
            }
        }
    }
}