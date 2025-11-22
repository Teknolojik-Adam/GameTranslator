using System.Windows;

namespace P5S_ceviri
{
    public partial class InputDialog : Window
    {
        public InputDialog(string question, string defaultAnswer = "")
        {
            InitializeComponent();

            // Eğer MainWindow'dan dolu bir soru metni geldiyse onu yaz.
            // Boş geldiyse XAML'daki "{DynamicResource Str_Input_Question}" geçerli kalır.
            if (!string.IsNullOrEmpty(question))
            {
                lblQuestion.Text = question;
            }

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