using System.Windows;
using System.Windows.Input;

namespace LoLTranslator.App;

public partial class PlayerFeedbackWindow : Window
{
    private sealed record IssueOption(string DisplayName, string Value);

    public PlayerFeedbackSubmission? Submission { get; private set; }

    public PlayerFeedbackWindow()
    {
        InitializeComponent();
        IssueTypeComboBox.ItemsSource = new[]
        {
            new IssueOption("安装、启动或模型问题", "setup_or_model"),
            new IssueOption("OCR 识别错误", "ocr_error"),
            new IssueOption("漏掉消息", "missed_message"),
            new IssueOption("重复翻译", "duplicate_translation"),
            new IssueOption("翻译不准确", "translation_error"),
            new IssueOption("游戏术语或黑话错误", "game_term_error"),
            new IssueOption("界面或操作问题", "display_or_usability"),
            new IssueOption("性能、卡顿或崩溃", "performance_or_crash"),
            new IssueOption("其他问题或建议", "other")
        };
        IssueTypeComboBox.SelectedIndex = 0;
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        if (IssueTypeComboBox.SelectedItem is not IssueOption issue) return;
        if (string.IsNullOrWhiteSpace(DescriptionTextBox.Text))
        {
            MessageBox.Show("请简单说明实际发生了什么。", "玩家反馈", MessageBoxButton.OK, MessageBoxImage.Information);
            DescriptionTextBox.Focus();
            return;
        }

        Submission = PlayerFeedbackService.CreateSubmission(
            issue.Value,
            DescriptionTextBox.Text,
            ReproductionTextBox.Text);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
