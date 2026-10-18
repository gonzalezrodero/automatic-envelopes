output "api_base" {
  value = "https://${var.api_hostname}"
}

output "name_servers" {
  value = aws_route53_zone.api.name_servers
}
