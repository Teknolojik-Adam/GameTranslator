using System.Windows;

namespace P5S_ceviri
{
    public partial class InputDialog : Window
    {
        public InputDialog(string question, string defaultAnswer = "")
        {
            InitializeComponent();
            lblQuestion.Text = question;
            txtAnswer.Text = defaultAnswer;
        }

        private void btnDialogOk_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
        }

        private void Window_ContentRendered(object sender, System.EventArgs e)
        {
            txtAnswer.SelectAll();
            txtAnswer.Focus();
        }

        public string Answer => txtAnswer.Text;
    }
}