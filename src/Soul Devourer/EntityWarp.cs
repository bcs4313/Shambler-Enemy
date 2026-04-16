using System;
using System.Collections.Generic;
using System.Text;

namespace SoulDev
{
    using UnityEngine;
    using UnityEngine.AI;
    internal class EntityWarp
    {
        // updates on map load
        public static EntranceTeleport[] mapEntrances;

        public struct entrancePack
        {
            public EntranceTeleport tele;
            public Vector3 navPosition;
        }

        // Find nearest entrance
        public static entrancePack findNearestEntrance(EnemyAI __instance)
        {
            float bestDistance = 99999999f;
            EntranceTeleport bestTele = null;
            EntranceTeleport[] array = mapEntrances;
            for (int j = 0; j < array.Length; j++)
            {
                if (__instance.isOutside == array[j].isEntranceToBuilding && Vector3.Distance(__instance.transform.position, array[j].transform.position) < bestDistance)
                {
                    bestDistance = Vector3.Distance(__instance.transform.position, array[j].transform.position);
                    bestTele = array[j];
                }
            }

            var pack = new entrancePack();

            // get a navigation position for the entrance
            if (bestTele != null)
            {
                NavMeshHit hit;
                var result = NavMesh.SamplePosition(bestTele.transform.position, out hit, 10f, NavMesh.AllAreas);
                if (result) { pack.navPosition = hit.position; }

            }
            pack.tele = bestTele;

            return pack;
        }

        // Send AI Outside/Inside Code
        public static void SendEnemyInside(EnemyAI __instance)
        {

            __instance.isOutside = false;
            __instance.allAINodes = GameObject.FindGameObjectsWithTag("AINode");

            //--- FIND BEST ENTRANCE ---
            EntranceTeleport doorEntered = findNearestEntrance(__instance).tele;

            if (!doorEntered)
            {
                Debug.LogError("MOAI EntranceTeleport: Failed to find entrance teleport.");
            }

            var entrancePosition = doorEntered.entrancePoint;

            if (!entrancePosition)
            {
                Debug.LogError("MOAI EntranceTeleport: Failed to find best exit position.");
            }

            NavMeshHit hit;
            var result = NavMesh.SamplePosition(entrancePosition.transform.position, out hit, 10f, NavMesh.AllAreas);
            if (result)
            {
                __instance.serverPosition = hit.position;
                __instance.transform.position = hit.position;
                __instance.agent.Warp(__instance.serverPosition);
                __instance.SyncPositionToClients();
            }
            else
            {
                Debug.LogError("MOAI EntranceTeleport: Failed to find exit NavmeshHit position");

            }
        }

        public static Transform findExitPoint(EntranceTeleport referenceDoor)
        {
            return referenceDoor.exitPoint;
        }

        public static void SendEnemyOutside(EnemyAI __instance, bool SpawnOnDoor = true)
        {
            __instance.isOutside = true;
            __instance.allAINodes = GameObject.FindGameObjectsWithTag("OutsideAINode");

            //--- FIND BEST ENTRANCE ---
            EntranceTeleport doorEntered = findNearestEntrance(__instance).tele;

            if (!doorEntered)
            {
                Debug.LogError("MOAI EntranceTeleport: Failed to find entrance teleport.");
            }

            var entrancePosition = doorEntered.entrancePoint;

            if (!entrancePosition)
            {
                Debug.LogError("MOAI EntranceTeleport: Failed to find best exit position.");
            }

            NavMeshHit hit;
            var result = NavMesh.SamplePosition(entrancePosition.transform.position, out hit, 10f, NavMesh.AllAreas);
            if (result)
            {
                __instance.serverPosition = hit.position;
                __instance.transform.position = hit.position;
                __instance.agent.Warp(__instance.serverPosition);
                __instance.SyncPositionToClients();
            }
            else
            {
                Debug.LogError("MOAI EntranceTeleport: Failed to find exit NavmeshHit position");

            }
        }
    }

}
