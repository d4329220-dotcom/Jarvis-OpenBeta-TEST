using System.Windows;
using System.Windows.Controls;
using Jarvis.Actions;
using Jarvis.Core;
using Jarvis.Voice;

namespace Jarvis.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly SecretStore _secrets;
    private readonly AssistantEngine _engine;

    private static readonly (string Name, int Vk)[] PttKeys =
    {
        ("Выключено", 0), ("F6", 0x75), ("F7", 0x76), ("F8", 0x77), ("F9", 0x78), ("F10", 0x79), ("F11", 0x7A), ("F12", 0x7B),
        ("Insert", 0x2D), ("Pause", 0x13), ("Scroll Lock", 0x91), ("Caps Lock", 0x14)
    };

    public SettingsWindow(AppSettings settings, SecretStore secrets, AssistantEngine engine)
    {
        InitializeComponent();
        _s = settings; _secrets = secrets; _engine = engine;

        Fill(MicBox, JarvisSpeechRecognizer.ListDevices(), _s.MicDevice);
        Fill(OutBox, JarvisSpeechSynthesizer.ListOutputDevices(), _s.OutputDevice);
        VoiceBox.Items.Add(new ComboBoxItem { Content = "Авто (русский)", Tag = "" });
        foreach (var v in JarvisSpeechSynthesizer.ListVoices())
        {
            var item = new ComboBoxItem { Content = v, Tag = v };
            VoiceBox.Items.Add(item);
            if (v == _s.VoiceName) VoiceBox.SelectedItem = item;
        }
        if (VoiceBox.SelectedItem == null) VoiceBox.SelectedIndex = 0;

        VolumeSlider.Value = _s.Volume; RateSlider.Value = _s.SpeechRate;
        NameBox.Text = _s.AssistantName; WakeBox.Text = _s.WakeWord;
        foreach (var (name, vk) in PttKeys)
        {
            var item = new ComboBoxItem { Content = name, Tag = vk };
            PttBox.Items.Add(item);
            if (vk == _s.PushToTalkKey) PttBox.SelectedItem = item;
        }
        if (PttBox.SelectedItem == null) PttBox.SelectedIndex = 0;
        ContinuousCheck.IsChecked = _s.ContinuousListening; TimeoutSlider.Value = _s.CommandTimeoutSeconds;
        SynonymsBox.Text = SynonymStore.Instance.ToText();
        LlmCheck.IsChecked = _s.LlmEnabled; EndpointBox.Text = _s.LlmEndpoint; ModelBox.Text = _s.LlmModel;
        AutoStartCheck.IsChecked = _s.AutoStart; TrayCheck.IsChecked = _s.StartInTray;
    }

    private static void Fill(ComboBox box, List<(int Index, string Name)> devices, int current)
    {
        var def = new ComboBoxItem { Content = "По умолчанию", Tag = -1 };
        box.Items.Add(def);
        box.SelectedItem = def;
        foreach (var (index, name) in devices)
        {
            var item = new ComboBoxItem { Content = name, Tag = index };
            box.Items.Add(item);
            if (index == current) box.SelectedItem = item;
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        _s.MicDevice = (int)((ComboBoxItem)MicBox.SelectedItem).Tag;
        _s.OutputDevice = (int)((ComboBoxItem)OutBox.SelectedItem).Tag;
        _s.VoiceName = (string)((ComboBoxItem)VoiceBox.SelectedItem).Tag;
        _s.PushToTalkKey = (int)((ComboBoxItem)PttBox.SelectedItem).Tag;
        _s.Volume = (int)VolumeSlider.Value; _s.SpeechRate = (int)RateSlider.Value;
        _s.AssistantName = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Джарвис";
        _s.WakeWord = WakeBox.Text.Trim().Length > 0 ? WakeBox.Text.Trim() : "джарвис";
        _s.ContinuousListening = ContinuousCheck.IsChecked == true; _s.CommandTimeoutSeconds = (int)TimeoutSlider.Value;
        _s.LlmEnabled = LlmCheck.IsChecked == true; _s.LlmEndpoint = EndpointBox.Text.Trim(); _s.LlmModel = ModelBox.Text.Trim();
        _s.AutoStart = AutoStartCheck.IsChecked == true; _s.StartInTray = TrayCheck.IsChecked == true;
        try
        {
            _s.Save();
            SynonymStore.Instance.SetFromText(SynonymsBox.Text);      // синонимы применяются сразу, без перезапуска
            if (KeyBox.Password.Length > 0) _secrets.SetApiKey(KeyBox.Password);
            SystemManager.SetAutostart(_s.AutoStart);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "save settings");
            MessageBox.Show("Не удалось сохранить настройки: " + ex.Message);
            return;
        }
        _engine.ApplySettings();
        DialogResult = true;
    }
}
