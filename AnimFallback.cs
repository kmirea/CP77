using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NAudio.MediaFoundation;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using WolvenKit.Core.Extensions;
using WolvenKit.Modkit.RED4.Animation;
using WolvenKit.Modkit.RED4.GeneralStructs;
using WolvenKit.Modkit.RED4.RigFile;
using WolvenKit.RED4.Archive;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

using static WolvenKit.RED4.Types.Enums;
using static WolvenKit.Modkit.RED4.Animation.Const;
using static WolvenKit.Modkit.RED4.Animation.Fun;
using static WolvenKit.Modkit.RED4.Animation.Gltf;
using WolvenKit.Common.Model.Arguments;

using fallbackTranslationFrames = System.Collections.Generic.Dictionary<ushort, Dictionary<ushort, System.Numerics.Vector3>>;
using fallbackRotationFrames = System.Collections.Generic.Dictionary<ushort, Dictionary<ushort, System.Numerics.Quaternion>>;

namespace WolvenKit.Modkit.RED4
{
    public enum AnimFallbackCompression
    {
        Uncompressed,
        //3x16bit w-stripped
        Quat3x16bit,
        //Fallback Frames (Medium)
        //-- Pos --Vec3 uncompressed
        //-- Rot - 16 * 4 x,y,z,w 
        Quat4x16bit,
        //Fallback Frames (High) 
        //-- Pos = 10:10:12 x,y,z
        Vec32_10bit
        //Fallback Frames (High) 
        //-- Rot = 3x16bit w-stripped
        ///tbd
    }
    public partial class ModTools
    {
        public Vector3 U32toVec3(uint u32)
        {
            //packed 10/10/12 bits xyz
            //TODO: zyx or xyz ??
            var z = Convert.ToSingle(u32 & 0xfff);
            var y = Convert.ToSingle((u32 >> 12) & 0x3ff);
            var x = Convert.ToSingle((u32 >> 22) & 0x3ff);
            // 1023 = (1 << 10) - 1
            var dequant = 1023f;
            x = (x / dequant) * 2f - 1f;
            y = (y / dequant) * 2f - 1f;
            z = (z / dequant) * 2f - 1f;
            return new Vector3(x, y, z);
        }
        //TODO: Reliably detect if animRig is player specific
        //Alternatives:  LoadRig -> bone count > 72  or bonelist has player specific bones (ik/)
        public bool isPlayerRig(string rigFileName="")
        {
            return new[] { "player", "pma", "pwa", "tpp" }.Any(str => rigFileName.ToLower().Contains(str))
        }
        public bool GetFallbackAnimation(ref animAnimSet anims)// out arg for processed buffer ?
        {
            
            var rigFileName = anims.Rig.DepotPath.GetResolvedText() ?? "<unknown rig depotpath??>";
            //Frame => BoneIdx => Transform
            var fallbackTranslationFrames = new Dictionary<ushort, Dictionary<ushort, Vector3>>();
            var fallbackRotationFrames = new Dictionary<ushort, Dictionary<ushort, Quaternion>>();
            //translations = <Frame, <Bone, Vec3>>
            //rotations = <Frame, <Bone, Quat>>

            //Contains Falback Frame Buffer ?
            MemoryStream fbdata;
            if(anims.FallbackAnimDataBuffer != null)
            {
                fbdata = new MemoryStream( anims.FallbackAnimDataBuffer.GetBytes());
                fbdata.Seek(0, SeekOrigin.Begin);
                var br = new BinaryReader(fbdata);

                var numFrames = anims.FallbackDataAddressIndexes.Count;
                var numPositions = anims.FallbackNumPositionData;
                var numRotations = anims.FallbackNumRotationData;
                var numTracks = anims.FallbackNumFloatTrackData;

                var translations = new List<System.Numerics.Vector3>();
                var rotations = new List<System.Numerics.Quaternion>();

                var indices_offset = 0;
                var indices_chunkSize = 0;
                var animkeys_offset = 0;
                var animkeys_chunkSize = 0;
                var positions_offset = 0;
                var rotations_offset = 0;
                 //fallback float compression
                 // Fallback Frame Float Compression Type
                // Player = LOD 0,2,3 bones,  | Non-Player LOD 0 bones only
                // Player = rot 16x4 pos 32 | Non-Player = rot 16x3-w pos float[3] raw
                var pos_bitsize = isPlayerRig() ? 32 : 96;
                var rot_bitsize = isPlayerRig() ? 64 : 48;

                _loggerService.Debug($"Exporting {numFrames} Fallback Frames for with {numPositions + numRotations} T/R transforms and {numTracks} tracks");

                // Get end of Indices /  start of Transform Data
                // dataAddresses are buffer size of each Frames Indices
                // -- uint16 [positionIndex]
                //--- uint16 boneIdx
                for(int f = 0; f < numFrames; f++)
                {
                    indices_chunkSize += (int)anims.fallbackDataAddresses[anims.fallbackDataAddressIndexes[f]];
                }
                //end of indices = start of compressed transforms
                animkeys_offset = indices_chunkSize;
            
                animkeys_chunkSize = ((pos_bitsize * numPositions)+ (rot_bitsize * numRotations)) / 8;

                if((fbdata.Length - animkeys_offset) != animkeys_offset)
                {
                    throw new Exception("Fallback Buffersize mismatch");
                }
                positions_offset = animkeys_offset;
                rotations_offset = positions_offset + ((pos_bitsize * numPositions)/8);

                br.Seek(positions_offset, SeekOrigin.Begin);

                //Read & Unpack all Translations
                for(int i = 0;  i < numPositions; i++)
                {
                    if(pos_bitsize == 32)
                    {
                        //player only
                        //10:10:12 packed xyz
                        translations.Add(U32toVec3(br.ReadUInt32()));
                    }
                    else
                    {
                        //raw float[3]
                        var x,y,z = br.ReadSingle();
                        translations.Add(new Vector3(x,y,z));
                    }
                }
                if((br.position != rotations_offset)
                {
                    throw new Exception("fallback rotations offset mismatch");
                }
                //Read & Unpack all Rotations
                for(int i = 0;  i < numRotations; i++)
                {
                    if(rot_bitsize == 64)
                    {
                        //player  only
                        //4*16bit packed xyzw quat
                        var x,y,z,w = br.readUInt16();
                        //todo: dequant
                        //rotations.Add();
                    }
                    else
                    {
                        //3*16bit packed xyz quat w-stripped
                        var x,y,z = br.readUInt16();
                        //todo: dequant
                        //rotations.Add();
                    }
                }
                //Now we can read Indices, & sort transforms by Bone/Frame
                br.Seek(0, SeekOrigin.Begin);
                for(int f = 0; f < numFrames; f++)
                {
                    var np = anims.FallbackAnimFrameDescs[anims.FallbackAnimDescIndexes[f]].mPositions;
                    var nr = anims.FallbackAnimFrameDescs[anims.FallbackAnimDescIndexes[f]].mRotations;
                    var pidx = new ushort[np];
                    var pbones = new ushort[np];
                    var ridx = new ushort[nr];
                    var rbones = new ushort[nr];
                    for(int i = 0; i < np;i++)
                    {
                        pidx[i] = br.ReadUint16();
                    }
                    for(int i = 0; i < np;i++)
                    {
                        pbones[i] = br.ReadUint16();
                    }
                    for(int i = 0; i < nr;i++)
                    {
                        ridx[i] = br.ReadUint16();
                    }
                    for(int i = 0; i < nr;i++)
                    {
                        rbones[i] = br.ReadUint16();
                    }
                    //assign to frame
                    for(int i = 0; i < np;i++)
                    {
                        fallbackTranslationFrames[f][pbones[i]] = translations[pidx[i]]; //.copy ?
                    }
                    for(int i = 0; i < nr;i++)
                    {
                        fallbackRotationFrames[f][rbones[i]] = rotations[ridx[i]]; //.copy ?
                    }
                }
            }
            //TODO : Find sample with FallBackTrackFloats
        }
    }
}