﻿using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Latios.Myri
{
    internal static class InitUpdateDestroy
    {
        //Parallel
        [BurstCompile]
        public struct DestroyOneshotsWhenFinishedJob : IJobEntityBatch
        {
            public DestroyCommandBuffer.ParallelWriter                dcb;
            [ReadOnly] public ComponentTypeHandle<AudioSourceOneShot> oneshotHandle;
            [ReadOnly] public EntityTypeHandle                        entityHandle;
            [ReadOnly] public NativeReference<int>                    audioFrame;
            [ReadOnly] public ComponentDataFromEntity<AudioSettings>  settingsCdfe;
            public Entity                                             worldBlackboardEntity;
            public int                                                sampleRate;
            public int                                                samplesPerSubframe;

            public void Execute(ArchetypeChunk chunk, int chunkIndex)
            {
                var oneshots        = chunk.GetNativeArray(oneshotHandle);
                var entities        = chunk.GetNativeArray(entityHandle);
                var lastAudioFrame  = audioFrame.Value - 1;
                int samplesPerFrame = samplesPerSubframe * settingsCdfe[worldBlackboardEntity].audioSubframesPerFrame;
                for (int i = 0; i < oneshots.Length; i++)
                {
                    var    os           = oneshots[i];
                    int    playedFrames = lastAudioFrame - os.m_spawnedAudioFrame;
                    double resampleRate = os.clip.Value.sampleRate / (double)sampleRate;
                    if (os.isInitialized && os.clip.Value.samplesLeftOrMono.Length < resampleRate * playedFrames * samplesPerFrame)
                    {
                        dcb.Add(entities[i], chunkIndex);
                    }
                }
            }
        }

        //Single
        [BurstCompile]
        public struct UpdateListenersJob : IJobEntityBatch
        {
            [ReadOnly] public ComponentTypeHandle<AudioListener> listenerHandle;
            [ReadOnly] public ComponentTypeHandle<Translation>   translationHandle;
            [ReadOnly] public ComponentTypeHandle<Rotation>      rotationHandle;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld>  ltwHandle;
            public NativeList<ListenerWithTransform>             listenersWithTransforms;

            public void Execute(ArchetypeChunk chunk, int chunkIndex)
            {
                var listeners = chunk.GetNativeArray(listenerHandle);
                if (chunk.Has(ltwHandle))
                {
                    var ltws = chunk.GetNativeArray(ltwHandle);
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var l = listeners[i];
                        //This culling desyncs the listener indices from the graph handling logic.
                        //Todo: Figure out how to bring this optimization back.
                        //if (l.volume > 0f)
                        {
                            l.itdResolution                                                  = math.clamp(l.itdResolution, 0, 15);
                            var ltw                                                          = ltws[i];
                            var transform                                                    = new RigidTransform(quaternion.LookRotation(ltw.Forward, ltw.Up), ltw.Position);
                            listenersWithTransforms.Add(new ListenerWithTransform { listener = l, transform = transform });
                        }
                    }
                }
                else
                {
                    bool                     hasTranslation = chunk.Has(translationHandle);
                    bool                     hasRotation    = chunk.Has(rotationHandle);
                    NativeArray<Translation> translations   = default;
                    NativeArray<Rotation>    rotations      = default;
                    if (hasTranslation)
                        translations = chunk.GetNativeArray(translationHandle);
                    if (hasRotation)
                        rotations = chunk.GetNativeArray(rotationHandle);
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        var l = listeners[i];
                        //This culling desyncs the listener indices from the graph handling logic.
                        //Todo: Figure out how to bring this optimization back.
                        //if (l.volume > 0f)
                        {
                            l.itdResolution = math.max(l.itdResolution, 0);

                            var transform = RigidTransform.identity;
                            if (hasRotation)
                                transform.rot = rotations[i].Value;
                            if (hasTranslation)
                                transform.pos = translations[i].Value;

                            listenersWithTransforms.Add(new ListenerWithTransform { listener = l, transform = transform });
                        }
                    }
                }
            }
        }

        //Parallel
        //Todo: It might be worth it to cull here rather than write to the emitters array.
        [BurstCompile]
        public struct UpdateOneshotsJob : IJobEntityBatchWithIndex
        {
            public ComponentTypeHandle<AudioSourceOneShot>                oneshotHandle;
            [ReadOnly] public ComponentTypeHandle<AudioSourceEmitterCone> coneHandle;
            [ReadOnly] public ComponentTypeHandle<Translation>            translationHandle;
            [ReadOnly] public ComponentTypeHandle<Rotation>               rotationHandle;
            [ReadOnly] public ComponentTypeHandle<Parent>                 parentHandle;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld>           ltwHandle;
            public NativeArray<OneshotEmitter>                            emitters;
            [ReadOnly] public NativeReference<int>                        audioFrame;
            [ReadOnly] public NativeReference<int>                        lastConsumedBufferId;
            public int                                                    bufferId;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var oneshots = chunk.GetNativeArray(oneshotHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var oneshot = oneshots[i];
                    //There's a chance the one shot spawned last game frame but the dsp missed the audio frame.
                    //In such a case, we still want the one shot to start at the beginning rather than skip the first audio frame.
                    //This is more likely to happen in high framerate scenarios.
                    //This does not solve the problem where the audio frame ticks during DSP and again before the next AudioSystemUpdate.
                    if ((!oneshot.isInitialized) | (oneshot.m_spawnedBufferId - lastConsumedBufferId.Value > 0))
                    {
                        oneshot.m_spawnedBufferId   = bufferId;
                        oneshot.m_spawnedAudioFrame = audioFrame.Value;
                        oneshots[i]                 = oneshot;
                    }
                }

                bool ltw = chunk.Has(ltwHandle);
                bool p   = chunk.Has(parentHandle);
                bool t   = chunk.Has(translationHandle);
                bool r   = chunk.Has(rotationHandle);
                bool c   = chunk.Has(coneHandle);

                int mask  = math.select(0, 0x10, ltw);
                mask     += math.select(0, 0x8, p);
                mask     += math.select(0, 0x4, t);
                mask     += math.select(0, 0x2, r);
                mask     += math.select(0, 0x1, c);

                //Note: We only care about rotation if there is also a cone
                switch (mask)
                {
                    case 0x0: ProcessNoTransform(chunk, firstEntityIndex); break;
                    case 0x1: ProcessCone(chunk, firstEntityIndex); break;
                    case 0x2: ProcessNoTransform(chunk, firstEntityIndex); break;
                    case 0x3: ProcessRotationCone(chunk, firstEntityIndex); break;
                    case 0x4: ProcessTranslation(chunk, firstEntityIndex); break;
                    case 0x5: ProcessTranslationCone(chunk, firstEntityIndex); break;
                    case 0x6: ProcessTranslation(chunk, firstEntityIndex); break;
                    case 0x7: ProcessTranslationRotationCone(chunk, firstEntityIndex); break;

                    case 0x8: ErrorCase(); break;
                    case 0x9: ErrorCase(); break;
                    case 0xa: ErrorCase(); break;
                    case 0xb: ErrorCase(); break;
                    case 0xc: ErrorCase(); break;
                    case 0xd: ErrorCase(); break;
                    case 0xe: ErrorCase(); break;
                    case 0xf: ErrorCase(); break;

                    case 0x10: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x11: ProcessCone(chunk, firstEntityIndex); break;
                    case 0x12: ProcessNoTransform(chunk, firstEntityIndex); break;
                    case 0x13: ProcessRotationCone(chunk, firstEntityIndex); break;
                    case 0x14: ProcessTranslation(chunk, firstEntityIndex); break;
                    case 0x15: ProcessTranslationCone(chunk, firstEntityIndex); break;
                    case 0x16: ProcessTranslation(chunk, firstEntityIndex); break;
                    case 0x17: ProcessTranslationRotationCone(chunk, firstEntityIndex); break;

                    case 0x18: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x19: ProcessLtwCone(chunk, firstEntityIndex); break;
                    case 0x1a: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x1b: ProcessLtwCone(chunk, firstEntityIndex); break;
                    case 0x1c: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x1d: ProcessLtwCone(chunk, firstEntityIndex); break;
                    case 0x1e: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x1f: ProcessLtwCone(chunk, firstEntityIndex); break;

                    default: ErrorCase(); break;
                }
            }

            void ProcessNoTransform(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var                    oneshots = chunk.GetNativeArray(oneshotHandle);
                AudioSourceEmitterCone cone     = default;
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new OneshotEmitter
                    {
                        source    = oneshots[i],
                        transform = RigidTransform.identity,
                        cone      = cone,
                        useCone   = false
                    };
                }
            }

            void ProcessCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var oneshots = chunk.GetNativeArray(oneshotHandle);
                var cones    = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new OneshotEmitter
                    {
                        source    = oneshots[i],
                        transform = RigidTransform.identity,
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            void ProcessRotationCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var oneshots  = chunk.GetNativeArray(oneshotHandle);
                var rotations = chunk.GetNativeArray(rotationHandle);
                var cones     = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new OneshotEmitter
                    {
                        source    = oneshots[i],
                        transform = new RigidTransform(rotations[i].Value, float3.zero),
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            void ProcessTranslation(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var                    oneshots     = chunk.GetNativeArray(oneshotHandle);
                var                    translations = chunk.GetNativeArray(translationHandle);
                AudioSourceEmitterCone cone         = default;
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new OneshotEmitter
                    {
                        source    = oneshots[i],
                        transform = new RigidTransform(quaternion.identity, translations[i].Value),
                        cone      = cone,
                        useCone   = false
                    };
                }
            }

            void ProcessTranslationCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var oneshots     = chunk.GetNativeArray(oneshotHandle);
                var translations = chunk.GetNativeArray(translationHandle);
                var cones        = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new OneshotEmitter
                    {
                        source    = oneshots[i],
                        transform = new RigidTransform(quaternion.identity, translations[i].Value),
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            void ProcessTranslationRotationCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var oneshots     = chunk.GetNativeArray(oneshotHandle);
                var translations = chunk.GetNativeArray(translationHandle);
                var rotations    = chunk.GetNativeArray(rotationHandle);
                var cones        = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new OneshotEmitter
                    {
                        source    = oneshots[i],
                        transform = new RigidTransform(rotations[i].Value, translations[i].Value),
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            void ProcessLtw(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var                    oneshots = chunk.GetNativeArray(oneshotHandle);
                var                    ltws     = chunk.GetNativeArray(ltwHandle);
                AudioSourceEmitterCone cone     = default;
                for (int i = 0; i < chunk.Count; i++)
                {
                    var ltw                        = ltws[i];
                    emitters[firstEntityIndex + i] = new OneshotEmitter
                    {
                        source    = oneshots[i],
                        transform = new RigidTransform(quaternion.LookRotationSafe(ltw.Forward, ltw.Up), ltw.Position),
                        cone      = cone,
                        useCone   = false
                    };
                }
            }

            void ProcessLtwCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var oneshots = chunk.GetNativeArray(oneshotHandle);
                var ltws     = chunk.GetNativeArray(ltwHandle);
                var cones    = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var ltw                        = ltws[i];
                    emitters[firstEntityIndex + i] = new OneshotEmitter
                    {
                        source    = oneshots[i],
                        transform = new RigidTransform(quaternion.LookRotationSafe(ltw.Forward, ltw.Up), ltw.Position),
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void ErrorCase()
            {
                throw new System.InvalidOperationException("OneshotsUpdateJob received an invalid EntityQuery");
            }
        }

        //Parallel
        //Todo: It might be worth it to cull here rather than write to the emitters array.
        [BurstCompile]
        public struct UpdateLoopedsJob : IJobEntityBatchWithIndex
        {
            public ComponentTypeHandle<AudioSourceLooped>                 loopedHandle;
            [ReadOnly] public ComponentTypeHandle<AudioSourceEmitterCone> coneHandle;
            [ReadOnly] public ComponentTypeHandle<Translation>            translationHandle;
            [ReadOnly] public ComponentTypeHandle<Rotation>               rotationHandle;
            [ReadOnly] public ComponentTypeHandle<Parent>                 parentHandle;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld>           ltwHandle;
            public NativeArray<LoopedEmitter>                             emitters;
            [ReadOnly] public NativeReference<int>                        audioFrame;
            [ReadOnly] public NativeReference<int>                        lastConsumedBufferId;
            public int                                                    bufferId;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                var seed   = math.asuint(audioFrame.Value);
                var looped = chunk.GetNativeArray(loopedHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var l = looped[i];

                    if (!l.m_initialized)
                    {
                        l.m_loopOffsetIndex = (int)(Squirrel3Noise((uint)(firstEntityIndex + i), seed) * (ulong)l.m_clip.Value.loopedOffsets.Length);
                        l.m_initialized     = true;
                        looped[i]           = l;
                    }
                }

                bool ltw = chunk.Has(ltwHandle);
                bool p   = chunk.Has(parentHandle);
                bool t   = chunk.Has(translationHandle);
                bool r   = chunk.Has(rotationHandle);
                bool c   = chunk.Has(coneHandle);

                int mask  = math.select(0, 0x10, ltw);
                mask     += math.select(0, 0x8, p);
                mask     += math.select(0, 0x4, t);
                mask     += math.select(0, 0x2, r);
                mask     += math.select(0, 0x1, c);

                //Note: We only care about rotation if there is also a cone
                switch (mask)
                {
                    case 0x0: ProcessNoTransform(chunk, firstEntityIndex); break;
                    case 0x1: ProcessCone(chunk, firstEntityIndex); break;
                    case 0x2: ProcessNoTransform(chunk, firstEntityIndex); break;
                    case 0x3: ProcessRotationCone(chunk, firstEntityIndex); break;
                    case 0x4: ProcessTranslation(chunk, firstEntityIndex); break;
                    case 0x5: ProcessTranslationCone(chunk, firstEntityIndex); break;
                    case 0x6: ProcessTranslation(chunk, firstEntityIndex); break;
                    case 0x7: ProcessTranslationRotationCone(chunk, firstEntityIndex); break;

                    case 0x8: ErrorCase(); break;
                    case 0x9: ErrorCase(); break;
                    case 0xa: ErrorCase(); break;
                    case 0xb: ErrorCase(); break;
                    case 0xc: ErrorCase(); break;
                    case 0xd: ErrorCase(); break;
                    case 0xe: ErrorCase(); break;
                    case 0xf: ErrorCase(); break;

                    case 0x10: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x11: ProcessCone(chunk, firstEntityIndex); break;
                    case 0x12: ProcessNoTransform(chunk, firstEntityIndex); break;
                    case 0x13: ProcessRotationCone(chunk, firstEntityIndex); break;
                    case 0x14: ProcessTranslation(chunk, firstEntityIndex); break;
                    case 0x15: ProcessTranslationCone(chunk, firstEntityIndex); break;
                    case 0x16: ProcessTranslation(chunk, firstEntityIndex); break;
                    case 0x17: ProcessTranslationRotationCone(chunk, firstEntityIndex); break;

                    case 0x18: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x19: ProcessLtwCone(chunk, firstEntityIndex); break;
                    case 0x1a: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x1b: ProcessLtwCone(chunk, firstEntityIndex); break;
                    case 0x1c: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x1d: ProcessLtwCone(chunk, firstEntityIndex); break;
                    case 0x1e: ProcessLtw(chunk, firstEntityIndex); break;
                    case 0x1f: ProcessLtwCone(chunk, firstEntityIndex); break;

                    default: ErrorCase(); break;
                }
            }

            //From https://www.youtube.com/watch?v=LWFzPP8ZbdU
            uint Squirrel3Noise(uint position, uint seed)
            {
                var val  = position * 0x68e31da4;
                val     *= seed;
                val     ^= (val >> 8);
                val     += 0xb5297a4d;
                val     ^= (val << 8);
                val     *= 0x1b56c4e9;
                val     ^= (val >> 8);
                return val;
            }

            void ProcessNoTransform(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var                    loopeds = chunk.GetNativeArray(loopedHandle);
                AudioSourceEmitterCone cone    = default;
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new LoopedEmitter
                    {
                        source    = loopeds[i],
                        transform = RigidTransform.identity,
                        cone      = cone,
                        useCone   = false
                    };
                }
            }

            void ProcessCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var loopeds = chunk.GetNativeArray(loopedHandle);
                var cones   = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new LoopedEmitter
                    {
                        source    = loopeds[i],
                        transform = RigidTransform.identity,
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            void ProcessRotationCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var loopeds   = chunk.GetNativeArray(loopedHandle);
                var rotations = chunk.GetNativeArray(rotationHandle);
                var cones     = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new LoopedEmitter
                    {
                        source    = loopeds[i],
                        transform = new RigidTransform(rotations[i].Value, float3.zero),
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            void ProcessTranslation(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var                    loopeds      = chunk.GetNativeArray(loopedHandle);
                var                    translations = chunk.GetNativeArray(translationHandle);
                AudioSourceEmitterCone cone         = default;
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new LoopedEmitter
                    {
                        source    = loopeds[i],
                        transform = new RigidTransform(quaternion.identity, translations[i].Value),
                        cone      = cone,
                        useCone   = false
                    };
                }
            }

            void ProcessTranslationCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var loopeds      = chunk.GetNativeArray(loopedHandle);
                var translations = chunk.GetNativeArray(translationHandle);
                var cones        = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new LoopedEmitter
                    {
                        source    = loopeds[i],
                        transform = new RigidTransform(quaternion.identity, translations[i].Value),
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            void ProcessTranslationRotationCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var loopeds      = chunk.GetNativeArray(loopedHandle);
                var translations = chunk.GetNativeArray(translationHandle);
                var rotations    = chunk.GetNativeArray(rotationHandle);
                var cones        = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    emitters[firstEntityIndex + i] = new LoopedEmitter
                    {
                        source    = loopeds[i],
                        transform = new RigidTransform(rotations[i].Value, translations[i].Value),
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            void ProcessLtw(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var                    loopeds = chunk.GetNativeArray(loopedHandle);
                var                    ltws    = chunk.GetNativeArray(ltwHandle);
                AudioSourceEmitterCone cone    = default;
                for (int i = 0; i < chunk.Count; i++)
                {
                    var ltw                        = ltws[i];
                    emitters[firstEntityIndex + i] = new LoopedEmitter
                    {
                        source    = loopeds[i],
                        transform = new RigidTransform(quaternion.LookRotationSafe(ltw.Forward, ltw.Up), ltw.Position),
                        cone      = cone,
                        useCone   = false
                    };
                }
            }

            void ProcessLtwCone(ArchetypeChunk chunk, int firstEntityIndex)
            {
                var loopeds = chunk.GetNativeArray(loopedHandle);
                var ltws    = chunk.GetNativeArray(ltwHandle);
                var cones   = chunk.GetNativeArray(coneHandle);
                for (int i = 0; i < chunk.Count; i++)
                {
                    var ltw                        = ltws[i];
                    emitters[firstEntityIndex + i] = new LoopedEmitter
                    {
                        source    = loopeds[i],
                        transform = new RigidTransform(quaternion.LookRotationSafe(ltw.Forward, ltw.Up), ltw.Position),
                        cone      = cones[i],
                        useCone   = true
                    };
                }
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            void ErrorCase()
            {
                throw new System.InvalidOperationException("LoopedsUpdateJob received an invalid EntityQuery");
            }
        }
    }
}

