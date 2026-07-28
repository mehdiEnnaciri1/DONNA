namespace Donna;

static class Program
{
    // GUID fixe pour éviter toute collision avec un mutex d'une autre application.
    private const string MutexName = "Donna-B7E1F2C4-9A3D-4E5F-8B6C-1D2E3F4A5B6C";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "DONNA est déjà en cours d'exécution (voir la barre des tâches).",
                "DONNA", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new DonnaContext());
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "DONNA — Erreur de démarrage", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
