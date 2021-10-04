﻿using Filetypes.ByteParsing;
using System;
using System.Collections.Generic;
using System.Text;

namespace FileTypes.Sound.WWise.Hirc.V122
{
    public class CAkDialogueEvent : HircItem
    {

        public byte uProbability;
        public uint uTreeDepth;
        public ArgumentList ArgumentList;
        public uint uTreeDataSize;
        public byte uMode;
        public AkDecisionTree AkDecisionTree;

        protected override void Create(ByteChunk chunk)
        {
            uProbability = chunk.ReadByte();
            uTreeDepth = chunk.ReadUInt32();
            ArgumentList = new ArgumentList(chunk, uTreeDepth);
            uTreeDataSize = chunk.ReadUInt32();
            uMode = chunk.ReadByte();
            AkDecisionTree = new AkDecisionTree(chunk, uTreeDepth);
        }
    }


    public class AkDecisionTree
    {
        public class Node
        {
            public uint key;
            public ushort children_uIdx;
            public ushort children_uCount;
            public ushort uWeight;
            public ushort uProbability;

            public List<Node> Children { get; set; } = new List<Node>();
            public List<SoundNode> SoundNodes { get; set; } = new List<SoundNode>();

            public Node()
            {
            }

            public void Parse(ByteChunk chunk, uint parentCount, uint currentTreeDepth, uint maxTreeDepth)
            {
                for (uint i = 0; i < parentCount; i++)
                {
                    var node = new Node()
                    {
                        key = chunk.ReadUInt32(),
                        children_uIdx = chunk.ReadUShort(),
                        children_uCount = chunk.ReadUShort(),
                        uWeight = chunk.ReadUShort(),
                        uProbability = chunk.ReadUShort(),
                    };

                    Children.Add(node);
                }

                foreach (var child in Children)
                {
                    if (currentTreeDepth != maxTreeDepth)
                        child.Parse(chunk, child.children_uCount, currentTreeDepth + 1, maxTreeDepth);
                    else
                    {
                        for(uint i = 0; i< child.children_uCount;i++)
                            child.SoundNodes.Add(new SoundNode(chunk));
                    }
                           
                }            
            }
        }

        public class SoundNode
        {
            public uint key;
            public uint audioNodeId;
            public ushort uWeight;
            public ushort uProbability;

            public SoundNode(ByteChunk chunk)
            {
                key = chunk.ReadUInt32();
                audioNodeId = chunk.ReadUInt32();
                uWeight = chunk.ReadUShort();
                uProbability = chunk.ReadUShort();
            }
        }

        public Node Root { get; set; }

        public AkDecisionTree(ByteChunk chunk, uint maxTreeDepth)
        {

            Root = new Node();
            Root.Parse(chunk, 1, 1, maxTreeDepth);
        }
    }

    public class ArgumentList
    {
        public List<Argument> Arguments { get; set; } = new List<Argument>();
        public ArgumentList(ByteChunk chunk, uint numItems)
        {
            for (uint i = 0; i < numItems; i++)
                Arguments.Add(new Argument(chunk));
        }

        public class Argument
        {
            public uint ulGroup { get; set; }
            public AkGroupType eGroupType { get; set; }
            public Argument(ByteChunk chunk)
            {
                ulGroup = chunk.ReadUInt32();
                eGroupType = (AkGroupType)chunk.ReadByte();
               
               
            }
        }
    }

  


    /*
      public class AkBankSourceData
    {
        public uint PluginId { get; set; }
        public ushort PluginId_type { get; set; }
        public ushort PluginId_company { get; set; }
        public SourceType StreamType { get; set; }

        public AkMediaInformation akMediaInformation { get; set; }
        public uint uSize { get; set; }
        public static AkBankSourceData Create(ByteChunk chunk)
        {
            var output = new AkBankSourceData()
            {
                PluginId = chunk.ReadUInt32(),
                //PluginId_type = chunk.ReadUShort(),
                //PluginId_company = chunk.ReadUShort(),
                StreamType = (SourceType)chunk.ReadByte()
            };

         
            output.PluginId_type = (ushort)((output.PluginId >> 0) & 0x000F);
            output.PluginId_company = (ushort)((output.PluginId >> 4) & 0x03FF);

            if (output.StreamType != SourceType.Straming)
            {
             //   throw new Exception();
            }

            if (output.PluginId_type == 0x02)
                output.uSize = chunk.ReadUInt32();

            output.akMediaInformation = AkMediaInformation.Create(chunk);

            return output;
        }
    }
     */
}
