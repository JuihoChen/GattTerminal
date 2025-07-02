using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The User Control item template is documented at http://go.microsoft.com/fwlink/?LinkId=234236

namespace GattTerminal
{
    public sealed partial class MyListView : Page
    {
        //public MyListView(object instance)
        public MyListView()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            MainPage that = e.Parameter as MainPage;
            DataContext = that;
        }

        private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DeviceInformationDisplay d = resultsListView.SelectedItem as DeviceInformationDisplay;
            if (d != null)
            {
                var msg = string.Empty;
                if (String.Compare(d.Name, "PT1000", true) == 0)
                {
                    msg = d.Id.Substring(d.Id.Length - 17);
                }
                ((MainPage)DataContext).syncListViewSrc.SetResult(msg);
            }
            Visibility = Visibility.Collapsed;
        }

    }
}
