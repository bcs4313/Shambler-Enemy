using GameNetcodeStuff;
using System;
using System.Collections.Generic;
using System.Text;
using static ExampleEnemy.Plugin;
using UnityEngine.AI;
using UnityEngine;

namespace ExampleEnemy.src.MoaiNormal
{
    internal class MoaiNormalNet
    {

        [Serializable]
        public class moaiSoundPkg
        {
            public ulong netId { get; set; }
            public string soundName { get; set; }

            public moaiSoundPkg(ulong _netId, string _soundName)
            {
                this.netId = _netId;
                this.soundName = _soundName;
            }
        }

        [Serializable]
        public class moaiSizePkg
        {
            public ulong netId { get; set; }
            public float size { get; set; }

            public float pitchAlter { get; set; }

            public moaiSizePkg(ulong _netId, float _size, float _pitchAlter)
            {
                this.netId = _netId;
                this.size = _size;
                this.pitchAlter = _pitchAlter;
            }
        }

        [Serializable]
        public class moaiAttachBodyPkg
        {
            public ulong netId { get; set; }
            public ulong humanNetId { get; set; }

            public moaiAttachBodyPkg(ulong _netId, ulong _humanNetId)
            {
                this.netId = _netId;
                this.humanNetId = _humanNetId;
            }
        }

        public static void setup()
        {
            LC_API.Networking.Network.RegisterMessage<moaiSoundPkg>("moaisoundplay", true, (long_identifier, moaiPkg) =>
            {
                // ai.NetworkObjectId synchronizes across moai
                ExampleEnemyAI target = null;
                Debug.Log("MOAI: received moaisound pkg from host: " + moaiPkg.netId.ToString() + " :: " + moaiPkg.soundName);
                ExampleEnemyAI[] moais = GameObject.FindObjectsOfType<ExampleEnemyAI>();
                for (int i = 0; i < moais.Length; i++)
                {
                    ExampleEnemyAI ai = moais[i];
                    if (ai.NetworkObjectId == moaiPkg.netId)
                    {
                        target = ai;
                    }
                }
                if (target == null)
                {
                    Debug.LogError("moaisoundplay call failed:: " + moaiPkg.netId.ToString() + " :: " + moaiPkg.soundName);
                    return;
                }

                switch (moaiPkg.soundName)
                {
                    case "creatureSFX":
                        target.stopAllSound();
                        target.creatureSFX.Play();
                        break;
                    case "creatureVoice":
                        target.stopAllSound();
                        target.creatureVoice.Play();
                        break;
                    case "creatureFood":
                        target.creatureSFX.Stop();
                        target.creatureVoice.Stop();
                        target.creatureFood.Play();
                        break;
                    case "creatureEat":
                        Debug.Log("Calling creatureEat on " + target + " :: " + target.creatureEat);
                        target.creatureSFX.Stop();
                        target.creatureVoice.Stop();
                        target.creatureEat.Play();
                        break;
                    case "creatureEatHuman":
                        Debug.Log("Calling creatureEatHuman on " + target + " :: " + target.creatureEatHuman);
                        target.creatureSFX.Stop();
                        target.creatureVoice.Stop();
                        target.creatureEatHuman.Play();
                        break;
                }
            });

            LC_API.Networking.Network.RegisterMessage<moaiSizePkg>("moaisizeset", true, (long_identifier, moaiSizePkg) =>
            {
                ExampleEnemyAI target = null;
                Debug.Log("MOAI: received moaisize pkg from host: " + moaiSizePkg.netId.ToString() + " :: " + moaiSizePkg.size);
                ExampleEnemyAI[] moais = GameObject.FindObjectsOfType<ExampleEnemyAI>();
                for (int i = 0; i < moais.Length; i++)
                {
                    ExampleEnemyAI ai = moais[i];
                    if (ai.NetworkObjectId == moaiSizePkg.netId)
                    {
                        target = ai;
                    }
                }
                if (target == null)
                {
                    Debug.LogError("moaisizeset call failed:: " + moaiSizePkg.netId.ToString() + " :: " + moaiSizePkg.size);
                    return;
                }
                target.gameObject.transform.localScale *= moaiSizePkg.size;
                target.gameObject.GetComponent<NavMeshAgent>().height *= moaiSizePkg.size;

                target.creatureSFX.pitch /= moaiSizePkg.pitchAlter;
                target.creatureVoice.pitch /= moaiSizePkg.pitchAlter;

                target.creatureFood = target.grabSource("CreatureFood");
                target.creatureEat = target.grabSource("CreatureEat");
                target.creatureEatHuman = target.grabSource("CreatureEatHuman");

                target.creatureFood.pitch /= moaiSizePkg.pitchAlter;
                target.creatureEat.pitch /= moaiSizePkg.pitchAlter;
                target.creatureEatHuman.pitch /= moaiSizePkg.pitchAlter;
            });

            LC_API.Networking.Network.RegisterMessage<moaiAttachBodyPkg>("moaiattachbody", true, (long_identifier, moaiAttachBodyPkg) =>
            {
                ExampleEnemyAI target = null;
                Debug.Log("MOAI: received moaiattachbody pkg from host: " + moaiAttachBodyPkg.netId.ToString() + " :: " + moaiAttachBodyPkg.humanNetId);
                ExampleEnemyAI[] moais = GameObject.FindObjectsOfType<ExampleEnemyAI>();
                for (int i = 0; i < moais.Length; i++)
                {
                    ExampleEnemyAI ai = moais[i];
                    if (ai.NetworkObjectId == moaiAttachBodyPkg.netId)
                    {
                        target = ai;
                    }
                }
                if (target == null)
                {
                    Debug.LogError("moaisizeset call failed:: " + moaiAttachBodyPkg.netId.ToString() + " :: " + moaiAttachBodyPkg.humanNetId);
                    return;
                }

                for (int i = 0; i < RoundManager.Instance.playersManager.allPlayerScripts.Length; i++)
                {
                    PlayerControllerB player = RoundManager.Instance.playersManager.allPlayerScripts[i];

                    if (player != null && player.name != null && player.transform != null)
                    {
                        if (player.NetworkObject.NetworkObjectId == moaiAttachBodyPkg.humanNetId)
                        {
                            Debug.Log("MOAI: Successfully attached body with id = " + moaiAttachBodyPkg.humanNetId);
                            player.deadBody.attachedLimb = player.deadBody.bodyParts[5];
                            player.deadBody.attachedTo = target.eye.transform;
                            player.deadBody.canBeGrabbedBackByPlayers = true;
                        }
                    }
                }

            });
        }
    }
}
