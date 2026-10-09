variable "hostname" {
  type        = string
  description = "Public API hostname, such as api.dev.core-webhook.eu."
}

variable "zone_id" {
  type        = string
  description = "Route 53 zone that will hold this hostname's records."
}

variable "origin_url" {
  type        = string
  description = "Lambda function URL, including the scheme."
}
