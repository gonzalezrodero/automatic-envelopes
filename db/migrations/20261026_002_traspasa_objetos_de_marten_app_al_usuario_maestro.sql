-- Pasa al usuario maestro los objetos de public cuyo dueño es marten_app.
-- Idempotente: si no hay ninguno, no cambia dueños. En una base nueva es un no-op.
-- migrate: transfers-ownership-from marten_app
--
-- Esas tablas (por ejemplo mt_doc_tenantmonthusage) las creó marten_app cuando
-- aun tenia CREATE en public. El maestro de RDS no es superusuario. En
-- PostgreSQL 16 el rol que creo marten_app queda como miembro con ADMIN OPTION,
-- pero sin INHERIT ni SET, y ALTER OWNER exige heredar los privilegios del
-- dueño actual. Esta migracion concede INHERIT solo para el traspaso y revoca
-- esa concesion (GRANTED BY el maestro), asi que el ADMIN OPTION que dejo
-- Terraform sigue igual.
-- Corre antes de la actualizacion de Marten 9.51, que hace ALTER y
-- CREATE OR REPLACE.

DO $transfer$
DECLARE
    obj record;
    granted_inherit boolean := false;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'marten_app') THEN
        RAISE NOTICE 'marten_app no existe; no hay nada que traspasar';
        RETURN;
    END IF;

    -- USAGE es true si ya hereda el rol (o si es superusuario). El miembro
    -- que creo Terraform no hereda, asi que aqui concede INHERIT.
    IF NOT pg_has_role(current_user, 'marten_app', 'USAGE') THEN
        EXECUTE format('GRANT marten_app TO %I WITH INHERIT TRUE', current_user);
        granted_inherit := true;
    END IF;

    FOR obj IN
        SELECT c.relkind, n.nspname AS schema_name, c.relname AS object_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relkind IN ('r', 'p', 'S', 'v', 'm', 'f', 'i', 'I')
          AND pg_get_userbyid(c.relowner) = 'marten_app'
          AND NOT EXISTS (
              SELECT 1 FROM pg_depend d
              WHERE d.classid = 'pg_class'::regclass
                AND d.objid = c.oid
                AND d.deptype = 'e'
          )
        ORDER BY c.relkind, n.nspname, c.relname
    LOOP
        IF obj.relkind = 'S' THEN
            EXECUTE format('ALTER SEQUENCE %I.%I OWNER TO %I', obj.schema_name, obj.object_name, current_user);
        ELSIF obj.relkind = 'v' THEN
            EXECUTE format('ALTER VIEW %I.%I OWNER TO %I', obj.schema_name, obj.object_name, current_user);
        ELSIF obj.relkind = 'm' THEN
            EXECUTE format('ALTER MATERIALIZED VIEW %I.%I OWNER TO %I', obj.schema_name, obj.object_name, current_user);
        ELSIF obj.relkind IN ('i', 'I') THEN
            EXECUTE format('ALTER INDEX %I.%I OWNER TO %I', obj.schema_name, obj.object_name, current_user);
        ELSE
            EXECUTE format('ALTER TABLE %I.%I OWNER TO %I', obj.schema_name, obj.object_name, current_user);
        END IF;
    END LOOP;

    FOR obj IN
        SELECT n.nspname AS schema_name,
               p.proname AS object_name,
               p.prokind,
               pg_get_function_identity_arguments(p.oid) AS args
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public'
          AND pg_get_userbyid(p.proowner) = 'marten_app'
          AND NOT EXISTS (
              SELECT 1 FROM pg_depend d
              WHERE d.classid = 'pg_proc'::regclass
                AND d.objid = p.oid
                AND d.deptype = 'e'
          )
        ORDER BY 1, 2, 4
    LOOP
        IF obj.prokind = 'p' THEN
            EXECUTE format(
                'ALTER PROCEDURE %I.%I(%s) OWNER TO %I',
                obj.schema_name, obj.object_name, obj.args, current_user);
        ELSIF obj.prokind = 'a' THEN
            EXECUTE format(
                'ALTER AGGREGATE %I.%I(%s) OWNER TO %I',
                obj.schema_name, obj.object_name, obj.args, current_user);
        ELSE
            EXECUTE format(
                'ALTER FUNCTION %I.%I(%s) OWNER TO %I',
                obj.schema_name, obj.object_name, obj.args, current_user);
        END IF;
    END LOOP;

    EXECUTE format('GRANT CONNECT ON DATABASE %I TO marten_app', current_database());
    GRANT USAGE, CREATE ON SCHEMA public TO marten_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO marten_app;
    GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO marten_app;

    -- Solo las funciones del maestro. GRANT ON ALL FUNCTIONS avisa, sin fallar,
    -- por cada funcion de pgvector, que es de la extension.
    FOR obj IN
        SELECT n.nspname AS schema_name,
               p.proname AS object_name,
               p.prokind,
               pg_get_function_identity_arguments(p.oid) AS args
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public'
          AND pg_get_userbyid(p.proowner) = current_user
        ORDER BY 1, 2, 4
    LOOP
        IF obj.prokind = 'p' THEN
            EXECUTE format(
                'GRANT EXECUTE ON PROCEDURE %I.%I(%s) TO marten_app',
                obj.schema_name, obj.object_name, obj.args);
        ELSE
            EXECUTE format(
                'GRANT EXECUTE ON FUNCTION %I.%I(%s) TO marten_app',
                obj.schema_name, obj.object_name, obj.args);
        END IF;
    END LOOP;

    IF granted_inherit THEN
        EXECUTE format('REVOKE marten_app FROM %I GRANTED BY %I', current_user, current_user);
    END IF;
END
$transfer$;
