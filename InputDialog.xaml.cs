using System;
using System.Windows;

namespace GameTranslatorUltimate
{
    public partial class InputDialog : Window
    {
        public InputDialog(
            string question,
            string defaultAnswer = "")
        {
            InitializeComponent();

            if (!string.IsNullOrWhiteSpace(question))
            {
                lblQuestion.Text =
                    question.Trim();
            }

            txtAnswer.Text =
                defaultAnswer ?? string.Empty;
        }

        public string Answer
        {
            get
            {
                return txtAnswer.Text ??
                       string.Empty;
            }
        }

        private void btnDialogOk_Click(
            object sender,
            RoutedEventArgs e)
        {
            DialogResult =
                true;
        }

        private void Window_ContentRendered(
            object sender,
            EventArgs e)
        {
            txtAnswer.SelectAll();
            txtAnswer.Focus();
        }
    }
}