variable "root_domain" {
  type = string
}

variable "route53_zone_id" {
  type    = string
  default = ""
}

variable "tags" {
  type    = map(string)
  default = {}
}
