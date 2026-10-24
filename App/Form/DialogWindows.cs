using System.Linq;
using System.Windows.Forms;

namespace MouseWithoutBorders;

internal static class DialogWindows
{
    internal static IWin32Window FindOwner()
    {
        // Never use the hidden, topmost input overlay as a dialog owner.
        var forms = Application.OpenForms.Cast<System.Windows.Forms.Form>()
            .Where(form => form.Visible && !form.IsDisposed && form is not FrmScreen && form is not FrmMouseCursor);
        return forms.FirstOrDefault(form => form.Modal) ?? forms.FirstOrDefault(form => form.ContainsFocus)
            ?? forms.FirstOrDefault(form => form is FrmMatrix) ?? forms.FirstOrDefault();
    }

    internal static DialogResult Show(System.Windows.Forms.Form dialog, IWin32Window owner = null)
    {
        if (owner is FrmScreen || owner is FrmMouseCursor) owner = null;
        owner ??= FindOwner();
        dialog.StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }
}
