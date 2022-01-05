﻿using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Text;

namespace CommonControls.FileTypes.MetaData.Definitions
{
    [MetaData("SOUND_TRIGGER", 10)]
    class SoundTrigger : MetaEntryBase
    {
        [MetaDataTag(5)]
        public string SoundEvent { get; set; }

        [MetaDataTag(6)]
        public int BoneIndex { get; set; }

        [MetaDataTag(7)]
        public Vector3 Position { get; set; }
    }
}