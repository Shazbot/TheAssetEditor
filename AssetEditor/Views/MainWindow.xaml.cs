﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using CommonControls.Common;
using KitbasherEditor.ViewModels;

namespace AssetEditor.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        Point _lastMouseDown;
        IEditorViewModel draggedItem;

        public MainWindow()
        {
            InitializeComponent();

            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);

            Title = $"AssetEditor v{fvi.FileMajorPart}.{fvi.FileMinorPart}";
            //Task.Run(async () =>
            //{
            //    while (true)
            //    {
            //        var totalMem = GC.GetTotalMemory(false) / 1_000_000;
            //        Dispatcher.Invoke(() =>
            //        {
            //            lblMemoryUsage.Text = $"Memory used = {totalMem} M";
            //        });
            //        await Task.Delay(10 * 1000);
            //    }
            //});
        }

        private void tabItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    _lastMouseDown = e.GetPosition(EditorsTabControl);

                    var item = (TabItem)sender;

                    item.Focusable = true;
                    item.Focus();
                    item.Focusable = false;

                    draggedItem = item.DataContext as IEditorViewModel;
                }
            }
            catch (Exception)
            {
            }
        }

        private void tabItem_MouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    Point currentPosition = e.GetPosition(EditorsTabControl);

                    if ((Math.Abs(currentPosition.X - _lastMouseDown.X) > 10.0) ||
                        (Math.Abs(currentPosition.Y - _lastMouseDown.Y) > 10.0))
                    {
                        if (draggedItem != null)
                        {
                            DragDrop.DoDragDrop(EditorsTabControl, EditorsTabControl.SelectedValue, DragDropEffects.Move);
                        }
                    }
                }
                else
                {
                    draggedItem = null;
                }
            }
            catch (Exception)
            {
            }
        }

        private void tabItem_Drop(object sender, DragEventArgs e)
        {
            try
            {
                var dropTargetItem = sender as TabItem;
                var pos = e.GetPosition(dropTargetItem);
                bool insertAfterTargetNode = pos.X-dropTargetItem.ActualWidth/2 > 0;

                if (DataContext is IDropTarget<IEditorViewModel, bool> dropContainer)
                {
                    if (draggedItem == null)
                        return;

                    var dropTargetNode = dropTargetItem?.DataContext as IEditorViewModel;
                    if (dropTargetNode == null)
                        return;

                    if (dropContainer.AllowDrop(draggedItem, dropTargetNode, insertAfterTargetNode))
                    {
                        dropContainer.Drop(draggedItem, dropTargetNode, insertAfterTargetNode);
                        e.Effects = DragDropEffects.None;
                        e.Handled = true;
                    }
                }
            }
            catch (Exception exception)
            {
            }
        }
    }
}
