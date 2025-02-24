﻿/*
 * Copyright (c) Gustave Monce and Contributors
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace UUPMediaCreator.UWP.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class InstallMediumTypePage : Page
    {
        public InstallMediumTypePage()
        {
            InitializeComponent();
        }

        private void WizardPage_NextClicked(object sender, RoutedEventArgs e)
        {
            if (ISO.IsChecked.Value)
            {
                App.ConversionPlan.InstallationMediumType = InstallationMediumType.ISO;
                _ = Frame.Navigate(typeof(AdditionalUpdatePage));
            }
            /*else if (InstallWindowsImage.IsChecked.Value)
            {
                App.ConversionPlan.InstallationMediumType = InstallationMediumType.InstallWIM;
                Frame.Navigate(typeof(FODPage));
            }
            else if (BootWindowsImage.IsChecked.Value)
            {
                App.ConversionPlan.InstallationMediumType = InstallationMediumType.BootWIM;
                Frame.Navigate(typeof(AdditionalUpdatePage));
            }
            else if (VirtualHardDisk.IsChecked.Value)
            {
                App.ConversionPlan.InstallationMediumType = InstallationMediumType.VHD;
                Frame.Navigate(typeof(FODPage));
            }*/
        }

        private void WizardPage_BackClicked(object sender, RoutedEventArgs e)
        {
            Frame.GoBack();
        }
    }
}