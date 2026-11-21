using System;
using System.Drawing;
using System.Windows.Forms;
using AgOpenGPS.Controls;

namespace AgOpenGPS.AgOpen
{
    /// <summary>
    /// Asks for one short piece of text (e.g. a new crop name), with AgOpenGPS's
    /// on-screen keyboard when that's turned on.
    /// </summary>
    public class FormAgOpenText : Form
    {
        private readonly TextBox textBox = new TextBox();
        private readonly Button buttonOk = new Button();
        private readonly Button buttonCancel = new Button();
        private readonly bool keyboard;

        public FormAgOpenText(string title, string question, string text)
        {
            keyboard = Properties.Settings.Default.setDisplay_isKeyboardOn;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(234, 237, 240);
            ControlBox = false;
            Font = new Font("Tahoma", 18F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Text = title;
            ClientSize = new Size(600, 250);

            var label = new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(24, 20, 552, 40),
                Text = question
            };

            textBox.Bounds = new Rectangle(24, 70, 552, 40);
            textBox.MaxLength = 40;
            textBox.Text = text ?? "";
            textBox.Click += (s, e) => ShowKeyboard();

            buttonOk.Bounds = new Rectangle(276, 160, 140, 66);
            buttonOk.Text = "OK";
            buttonOk.BackColor = Color.FromArgb(30, 115, 55);
            buttonOk.ForeColor = Color.White;
            buttonOk.FlatStyle = FlatStyle.Flat;
            buttonOk.Font = new Font("Tahoma", 18F, FontStyle.Bold);
            buttonOk.DialogResult = DialogResult.OK;

            buttonCancel.Bounds = new Rectangle(436, 160, 140, 66);
            buttonCancel.Text = "Cancel";
            buttonCancel.BackColor = Color.White;
            buttonCancel.FlatStyle = FlatStyle.Flat;
            buttonCancel.DialogResult = DialogResult.Cancel;

            Controls.Add(label);
            Controls.Add(textBox);
            Controls.Add(buttonOk);
            Controls.Add(buttonCancel);
            AcceptButton = buttonOk;
            CancelButton = buttonCancel;
        }

        /// <summary>What was typed, trimmed.</summary>
        public string Value
        {
            get { return textBox.Text.Trim(); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            textBox.Focus();
            ShowKeyboard();
        }

        private void ShowKeyboard()
        {
            if (!keyboard) return;
            textBox.ShowKeyboard(this);
            buttonOk.Focus();
        }
    }
}
