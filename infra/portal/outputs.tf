output "pages_project_name" {
  description = "Cloudflare Pages project name"
  value       = cloudflare_pages_project.admin.name
}

output "pages_subdomain" {
  description = "Default pages.dev hostname"
  value       = cloudflare_pages_project.admin.subdomain
}

output "portal_hostname" {
  description = "Custom hostname attached to the project, when set"
  value       = var.portal_hostname
}

output "api_base" {
  description = "API origin baked into the portal build"
  value       = local.api_base
}
