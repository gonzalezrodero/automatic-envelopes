using AutomaticEnvelopes.Api.Core.Entities;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;

namespace AutomaticEnvelopes.Api.Features.Knowledge;

public class HnswIndexCustomizer : IFeatureSchema
{
    public IEnumerable<Type> DependentTypes() => [typeof(DocumentChunk)];
    public ISchemaObject[] Objects => [];
    public string Identifier => "hnsw-vector-index";

    public Migrator Migrator => new PostgresqlMigrator();
    public Type StorageType => typeof(HnswIndexCustomizer);

    public void WritePermissions(Migrator rules, TextWriter writer) { }

    public void WriteTemplate(Migrator _, TextWriter writer)
    {
        // We use the public schema explicitly to avoid any search_path issues in Marten
        writer.WriteLine(@"
            CREATE INDEX IF NOT EXISTS mt_doc_documentchunk_idx_embedding 
            ON public.mt_doc_documentchunk USING hnsw (public.extract_embedding(data) vector_cosine_ops);
        ");
    }
}