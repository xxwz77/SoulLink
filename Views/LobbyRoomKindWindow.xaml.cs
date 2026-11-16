using System.Windows;
using System.Windows.Input;
using SoulLink.Models;

namespace SoulLink.Views
{
    /// <summary>新建房间时选择联机类型。</summary>
    public partial class LobbyRoomKindWindow : Window
    {
        public RoomKind SelectedKind { get; private set; } = RoomKind.PrivateFour;

        public LobbyRoomKindWindow()
        {
            InitializeComponent();
        }

        private void CardPrivateFour_Click(object sender, MouseButtonEventArgs e)
        {
            SelectedKind = RoomKind.PrivateFour;
            DialogResult = true;
        }

        private void CardPrivatePair_Click(object sender, MouseButtonEventArgs e)
        {
            SelectedKind = RoomKind.PrivatePair;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
