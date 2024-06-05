using UnityEngine;

namespace MoaiEnemy.src.Utilities
{
    // Credit to xCeezyx for SendEnemyInside/Outside code! makes my life so much easier.
    // https://github.com/xCeezyx/LethalEscape/tree/main?tab=readme-ov-file
    internal class EntityWarp
    {
        // Find nearest entrance
        public static EntranceTeleport findNearestEntrance(EnemyAI __instance)
        {
            float bestDistance = 99999999;
            EntranceTeleport bestTele = null;
            //--- FIND MAIN ENTERANCE ---
            EntranceTeleport[] array = Object.FindObjectsOfType<EntranceTeleport>(false);
            for (int j = 0; j < array.Length; j++)
            {
                if (Vector3.Distance(__instance.transform.position, array[j].transform.position) < bestDistance)
                {
                    bestDistance = Vector3.Distance(__instance.transform.position, array[j].transform.position);
                    bestTele = array[j];
                }
            }

            return bestTele;
        }

        // Send AI Outside/Inside Code
        public static void SendEnemyInside(EnemyAI __instance)
        {

            __instance.isOutside = false;
            __instance.allAINodes = GameObject.FindGameObjectsWithTag("AINode");


            //--- FIND BEST ENTRANCE ---
            EntranceTeleport[] array = Object.FindObjectsOfType<EntranceTeleport>(false);
            bool foundBest = false;
            for (int j = 0; j < array.Length; j++)
            {
                if (array[j].entranceId == 0 && !array[j].isEntranceToBuilding && !foundBest)
                {
                    __instance.serverPosition = array[j].entrancePoint.position;
                    break;
                }
                var exitPoint = findExitPoint(array[j]);
                if (exitPoint != null)
                {
                    __instance.serverPosition = array[j].entrancePoint.position;
                    foundBest = true;
                }
            }
            if (!foundBest)
            {
                Debug.Log("MOAI: Failed to find best exit position. Using default Entrance Teleport");
            }
            else
            {
                Debug.Log("MOAI: Teleporting to best possible exit.");
            }

            Transform ClosestNodePos = __instance.ChooseClosestNodeToPosition(__instance.serverPosition, false, 0);

            if (Vector3.Magnitude(ClosestNodePos.position - __instance.serverPosition) > 10)
            {
                __instance.serverPosition = ClosestNodePos.position;
                __instance.transform.position = __instance.serverPosition;
            }

            __instance.transform.position = __instance.serverPosition;

            __instance.agent.Warp(__instance.serverPosition);
            __instance.SyncPositionToClients();
        }

        public static Transform findExitPoint(EntranceTeleport referenceDoor)
        {
            EntranceTeleport[] array = Object.FindObjectsOfType<EntranceTeleport>();
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i].isEntranceToBuilding != referenceDoor.isEntranceToBuilding && array[i].entranceId == referenceDoor.entranceId)
                {
                    return array[i].entrancePoint;
                }
            }
            return null;
        }

        public static void SendEnemyOutside(EnemyAI __instance, bool SpawnOnDoor = true)
        {
            __instance.isOutside = true;
            __instance.allAINodes = GameObject.FindGameObjectsWithTag("OutsideAINode");

            //--- FIND ENTERANCE DOOR CLOSEST TO PLAYERS
            EntranceTeleport[] array = Object.FindObjectsOfType<EntranceTeleport>(false);
            float ClosestexitDistance = 999;
            for (int j = 0; j < array.Length; j++)
            {
                if (array[j].isEntranceToBuilding)
                {
                    for (int i = 0; i < StartOfRound.Instance.connectedPlayersAmount + 1; i++)
                    {
                        if (!StartOfRound.Instance.allPlayerScripts[i].isInsideFactory & Vector3.Magnitude(StartOfRound.Instance.allPlayerScripts[i].transform.position - array[j].entrancePoint.position) < ClosestexitDistance)
                        {
                            ClosestexitDistance = Vector3.Magnitude(StartOfRound.Instance.allPlayerScripts[i].transform.position - array[j].entrancePoint.position);
                            __instance.serverPosition = array[j].entrancePoint.position;
                        }
                    }

                }
            }

            if (__instance.OwnerClientId != GameNetworkManager.Instance.localPlayerController.actualClientId)
            {
                __instance.ChangeOwnershipOfEnemy(GameNetworkManager.Instance.localPlayerController.actualClientId);
            }

            Transform ClosestNodePos = __instance.ChooseClosestNodeToPosition(__instance.serverPosition, false, 0);

            if (Vector3.Magnitude(ClosestNodePos.position - __instance.serverPosition) > 10 || SpawnOnDoor == false)
            {
                __instance.serverPosition = ClosestNodePos.position;
            }
            __instance.transform.position = __instance.serverPosition;

            __instance.agent.Warp(__instance.serverPosition);
            __instance.SyncPositionToClients();

            if (GameNetworkManager.Instance.localPlayerController != null)
            {
                __instance.EnableEnemyMesh(!StartOfRound.Instance.hangarDoorsClosed || !GameNetworkManager.Instance.localPlayerController.isInHangarShipRoom, false);
            }
        }
    }
}
