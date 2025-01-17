﻿using CommonControls.FileTypes.PackFiles.Models;
using CommonControls.FileTypes.Sound;
using CommonControls.FileTypes.Sound.WWise.Bkhd;
using CommonControls.FileTypes.Sound.WWise.Hirc;
using Filetypes.ByteParsing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CommonControls.Editors.AudioEditor.BnkCompiler
{
    public class BnkFileManipulator
    {
        public ByteHirc FindHirc(ByteChunk wholeBnkChunk, uint hircId)
        {
            var header = BkhdParser.Create(wholeBnkChunk);
            while (wholeBnkChunk.BytesLeft != 0)
            {
                var chunckHeader = BnkChunkHeader.PeakFromBytes(wholeBnkChunk);
                var indexBeforeRead = wholeBnkChunk.Index;
                var expectedIndexAfterRead = indexBeforeRead + BnkChunkHeader.HeaderByteSize + chunckHeader.ChunkSize;

                if (chunckHeader.Tag == "HIRC")
                {
                    var hirchHeader = new HircChunk();
                    hirchHeader.ChunkHeader = BnkChunkHeader.CreateFromBytes(wholeBnkChunk);
                    hirchHeader.NumHircItems = wholeBnkChunk.ReadUInt32();

                    for (uint itemIndex = 0; itemIndex < hirchHeader.NumHircItems; itemIndex++)
                    {
                        // Read as unkown, we only care about size and ID
                        var item = new ByteHirc();
                        item.Parse(wholeBnkChunk);

                        if (item.Id == hircId)
                            return item;
                    }
                }
                else
                    throw new Exception();
                    
            }

            return null;
        }

        public byte[] ExtractHircToOwnBnk(ByteChunk wholeBnkChunk, uint hircId, string outputBnkNameWithoutExtention)
        {
            var header = BkhdParser.Create(wholeBnkChunk);
            header.dwSoundBankID = WWiseNameLookUpHelper.ComputeWWiseHash(outputBnkNameWithoutExtention);

            wholeBnkChunk.Index = 0;
            var hirc = FindHirc(wholeBnkChunk, hircId);
            var hirchChunk = new HircChunk();
            hirchChunk.SetFromHirc(hirc);

            var headerBytes = BkhdParser.GetAsByteArray(header);
            var hircBytes = new HircParser().GetAsBytes(hirchChunk);

            // Write
            using var memStream = new MemoryStream();
            memStream.Write(headerBytes);
            memStream.Write(hircBytes);
            var bytes = memStream.ToArray();

            var bnkPackFile = new PackFile(outputBnkNameWithoutExtention, new MemorySource(bytes));
            var result = Bnkparser.Parse(bnkPackFile, "test\\TestFile.bnk");

            return bytes;
        }

        public byte[] RemoveHirc(ByteChunk wholeBnkChunk, uint hircId)
        {
            var hirc = FindHirc(wholeBnkChunk, hircId);
            var hircStart = (int)hirc.IndexInFile;
            var hircLength = (int)hirc.Size;

            var byteArray = wholeBnkChunk.Buffer.ToList();
            byteArray.RemoveRange(hircStart, hircLength);

            return byteArray.ToArray();
        }

        byte[] ReplaceHirc()
        {
            throw new Exception();
        }

        byte[] AddHirc()
        {
            throw new Exception();
        }


        byte[] UpdateHeaders()
        {
            throw new Exception();
        }


    }
}
