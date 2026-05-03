using Microsoft.EntityFrameworkCore.Migrations;

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedAiPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var now = DateTimeOffset.UtcNow;

            migrationBuilder.InsertData(
                table: "AiPromptConfigs",
                columns: new[] { "Id", "Feature", "Channel", "Name", "Prompt", "IsActive", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    {
                        new Guid("10000000-0000-0000-0000-000000000001"),
                        "ml_title",
                        "mercadolivre",
                        "Sugerir título otimizado para ML",
                        "Você é especialista em SEO para Mercado Livre Brasil.\nReescreva e otimize o título abaixo para anúncio no ML.\nTítulo atual: \"{title}\"\nProduto: {name}, Marca: {brand}, Categoria: {category}.\nRegras: máximo 60 caracteres, sem emojis, sem exclamações, inclua palavras-chave relevantes, formato \"Marca + Produto + Diferencial\".\nResponda APENAS o título, sem explicações.",
                        true,
                        now,
                        now
                    },
                    {
                        new Guid("10000000-0000-0000-0000-000000000002"),
                        "ml_description",
                        "mercadolivre",
                        "Melhorar descrição para ML",
                        "Você é copywriter especializado em Mercado Livre Brasil.\nReescreva e melhore a descrição abaixo para anúncio no ML.\nProduto: {name}, Marca: {brand}.\nDescrição original: \"{description}\"\nRegras: plain text (sem HTML), parágrafos curtos, mantenha todas as especificações técnicas, destaque benefícios, adicione FAQ se relevante. Máximo 3000 caracteres.",
                        true,
                        now,
                        now
                    },
                    {
                        new Guid("10000000-0000-0000-0000-000000000003"),
                        "ml_sale_terms",
                        "mercadolivre",
                        "Sugerir garantia",
                        "Sugira o tipo e tempo de garantia ideal para este produto no Mercado Livre Brasil.\nProduto: {name}, Marca: {brand}, Categoria: {category}, Preço: R$ {price}.\nResponda em JSON com as chaves \"warrantyType\" (\"Garantia do fabricante\" ou \"Garantia do vendedor\") e \"warrantyTime\" (\"90 dias\", \"6 meses\", \"12 meses\" ou \"24 meses\").\nResponda APENAS o JSON, sem mais texto.",
                        true,
                        now,
                        now
                    },
                    {
                        new Guid("10000000-0000-0000-0000-000000000004"),
                        "ml_shipping",
                        "mercadolivre",
                        "Analisar frete grátis",
                        "Analise se frete grátis compensa para este produto no Mercado Livre Brasil.\nProduto: {name}, Preço: R$ {price}, Custo: R$ {cost}, Categoria: {category}.\nConsidere que frete grátis aumenta visibilidade mas reduz margem (custo médio de envio MLB: R$ 15-30 para produtos até 1kg).\nResponda em JSON com as chaves \"freeShipping\" (true/false) e \"reason\" (string curta explicando o motivo).\nResponda APENAS o JSON, sem mais texto.",
                        true,
                        now,
                        now
                    }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AiPromptConfigs",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "AiPromptConfigs",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "AiPromptConfigs",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "AiPromptConfigs",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"));
        }
    }
}
