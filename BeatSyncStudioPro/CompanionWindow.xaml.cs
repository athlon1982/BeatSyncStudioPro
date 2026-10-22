using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Companion control panel (v3.32): start/stop the phone server, see the
    /// addresses the phone can use (LAN = Wi-Fi door, 100.x = Tailscale
    /// door), and set/clear the password every connection must pass.
    /// </summary>
    public partial class CompanionWindow : Window
    {
        private readonly DjWindow _dj;

        public CompanionWindow(DjWindow dj)
        {
            InitializeComponent();
            _dj = dj;
            RefreshUi();
        }

        private void RefreshUi()
        {
            var server = _dj.Companion;
            var settings = CompanionServer.LoadSettings();
            if (server.IsRunning)
            {
                ServerStatusText.Text = $"● Server running on port {server.Port}";
                ServerToggleButton.Content = "Stop companion server";
            }
            else
            {
                ServerStatusText.Text = server.LastError != null
                    ? $"● Server stopped — last error: {server.LastError}"
                    : "● Server stopped";
                ServerToggleButton.Content = "Start companion server";
            }
            AddressesText.Text = BuildAddresses(settings.Port);
            PasswordStatusText.Text = server.PasswordRequired
                ? "Password is SET — phones must enter it before any control works."
                : "No password set — anyone who can reach an address above can control the decks. Set one to lock it down.";
            SessionsText.Text = server.IsRunning ? $"Phones signed in: {server.SessionCount}" : "";
        }

        private static string BuildAddresses(int port)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"This PC:      http://localhost:{port}");
            try
            {
                foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte b0 = ip.GetAddressBytes()[0], b1 = ip.GetAddressBytes()[1];
                    string label = b0 == 100 && b1 >= 64 && b1 <= 127 ? "Tailscale" : "Wi-Fi / LAN";
                    if (b0 == 127) continue;
                    sb.AppendLine($"{label}:  http://{ip}:{port}");
                }
            }
            catch { }
            return sb.ToString().TrimEnd();
        }

        private void ServerToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dj.Companion.IsRunning) _dj.StopCompanionServer();
            else _dj.StartCompanionServer();
            RefreshUi();
        }

        private void SetPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            string pw = PasswordInput.Password;
            if (string.IsNullOrWhiteSpace(pw))
            {
                MessageBox.Show("Type a password first — or use Clear to remove the gate.", "Phone Companion");
                return;
            }
            _dj.Companion.SetPassword(pw);
            PasswordInput.Password = "";
            RefreshUi();
        }

        private void ClearPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            _dj.Companion.SetPassword(null);
            PasswordInput.Password = "";
            RefreshUi();
        }
    }
}
