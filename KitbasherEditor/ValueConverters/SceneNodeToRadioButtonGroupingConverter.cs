﻿using CommonControls.Resources;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using View3D.Components.Component;

namespace KitbasherEditor.ValueConverters
{

    [ValueConversion(typeof(SceneNode), typeof(string))]
    public class SceneNodeToRadioButtonGroupingConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var sceneNode = value as SceneNode;

            if (sceneNode is Rmv2ModelNode)
                return sceneNode.Parent.Id;

            if (sceneNode is Rmv2LodNode)
                return sceneNode.Parent.Id;

            if(sceneNode is SlotNode)
                return Guid.NewGuid().ToString();

            return Guid.NewGuid().ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
