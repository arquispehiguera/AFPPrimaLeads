namespace AFPPrimaLeads.Core.Entities
{
    public class ProspectosResponse
    {
        public List<Prospecto> prospectos { get; set; } = new();
    }
    public class Prospecto
    {
        public string dni { get; set; } = string.Empty;
        public string primerNombre { get; set; } = string.Empty;
        public string? segundoNombre { get; set; }
        public string primerApellido { get; set; } = string.Empty;
        public string? segundoApellido { get; set; }
        public string? genero { get; set; }
        public string? email { get; set; }
        public string celular { get; set; } = string.Empty;
        public string? ultimoPaso { get; set; }
        public string? fechaUltimoPaso { get; set; }
        public string? canal { get; set; }
        public string? edad { get; set; }
        public string? fechaNacimiento { get; set; }
        public string? tipoComision { get; set; }
        public string? afpOrigen { get; set; }
        public string? indicadorEnPrima { get; set; }
        public string? tipoCliente { get; set; }
        public string? celularBcp { get; set; }
        public string? ramBcp { get; set; }
        public string? ramPrima { get; set; }
        public string? fechaAfiliacionPrima { get; set; }
        public bool errorValidacionReniec { get; set; }
        public string? parametrosUtm { get; set; }
        public string? jsonClient { get; set; }

        //Nuevos campos añadidos
        public string? utmSource { get; set; }
        public string? utmMedium { get; set; }
        public string? utmCampaign { get; set; }
        public string? utmContent { get; set; }
        public string NombreCompleto()
        {
            var parts = new[] { primerNombre, segundoNombre, primerApellido, segundoApellido }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(" ", parts);
        }
    }
}
