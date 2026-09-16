#!/usr/bin/env bash
set -e

# ==============================================================================
# Resono Turnkey All-in-One Installer
# https://github.com/Leo21mclt/Resono
# ==============================================================================

BOLD='\033[1m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${CYAN}"
cat << "EOF"
  ____                                
 |  _ \ ___  ___  ___  _ __   ___     
 | |_) / _ \/ __|/ _ \| '_ \ / _ \    
 |  _ <  __/\__ \ (_) | | | | (_) |   
 |_| \_\___||___/\___/|_| |_|\___/    
 The Self-Hosted Virtual Music Engine 
EOF
echo -e "${NC}"

echo -e "${BOLD}Welcome to the Resono Turnkey Stack Installer!${NC}"
echo -e "This script installs Jellyfin, Resono Gateway, and auto-configures the Resono plugin.\n"

# 1. Check prerequisites
echo -e "${YELLOW}--> Checking prerequisites...${NC}"

if ! command -v curl &> /dev/null; then
  echo -e "${RED}Error: curl is required but not installed. Please install curl and try again.${NC}"
  exit 1
fi

if ! command -v docker &> /dev/null; then
  echo -e "${RED}Error: Docker is not installed. Please install Docker first: https://docs.docker.com/engine/install/${NC}"
  exit 1
fi

# Detect Docker Compose command (v2 'docker compose' or v1 'docker-compose')
if docker compose version &> /dev/null; then
  COMPOSE_CMD="docker compose"
elif command -v docker-compose &> /dev/null; then
  COMPOSE_CMD="docker-compose"
else
  echo -e "${RED}Error: Docker Compose is not installed. Please install Docker Compose v2.${NC}"
  exit 1
fi

echo -e "${GREEN}✓ Docker and Docker Compose detected (${COMPOSE_CMD}).${NC}"

# 2. Target installation folder
INSTALL_DIR="${RESONO_DIR:-./resono}"
read -p "Install directory [default: ${INSTALL_DIR}]: " USER_DIR
if [ -n "$USER_DIR" ]; then
  INSTALL_DIR="$USER_DIR"
fi

mkdir -p "${INSTALL_DIR}"
cd "${INSTALL_DIR}"
echo -e "${GREEN}✓ Installing into: $(pwd)${NC}"

# 3. Create required directories
echo -e "${YELLOW}--> Setting up volume directories...${NC}"
mkdir -p config/jellyfin/plugins/Resono
mkdir -p cache/jellyfin
mkdir -p media/music
mkdir -p data

# 4. Download compose file and .env
echo -e "${YELLOW}--> Fetching latest docker-compose.yml and configuration...${NC}"
BASE_URL="https://raw.githubusercontent.com/Leo21mclt/Resono/main"

curl -fsSL "${BASE_URL}/docker-compose.yml" -o docker-compose.yml
echo -e "${GREEN}✓ Downloaded docker-compose.yml${NC}"

if [ ! -f .env ]; then
  curl -fsSL "${BASE_URL}/.env.example" -o .env
  echo -e "${GREEN}✓ Created .env template${NC}"
else
  echo -e "${CYAN}i Existing .env found, preserving configuration.${NC}"
fi

# 5. Fetch verified plugin DLL
echo -e "${YELLOW}--> Downloading latest Resono.Plugin.dll...${NC}"
curl -fsSL "${BASE_URL}/resono-gateway/plugin/Resono.Plugin.dll" -o config/jellyfin/plugins/Resono/Resono.Plugin.dll
chmod 755 config/jellyfin/plugins/Resono/Resono.Plugin.dll
echo -e "${GREEN}✓ Resono Jellyfin plugin bootstrapped successfully.${NC}"

# 6. Prompt to start services
echo ""
read -p "Do you want to start the Resono stack now? [Y/n]: " START_NOW
START_NOW=${START_NOW:-Y}

if [[ "$START_NOW" =~ ^[Yy]$ ]]; then
  echo -e "${YELLOW}--> Starting Resono Stack via ${COMPOSE_CMD}...${NC}"
  $COMPOSE_CMD pull || true
  $COMPOSE_CMD up -d

  # Determine host IP
  HOST_IP=$(hostname -I 2>/dev/null | awk '{print $1}' || echo "localhost")
  if [ -z "$HOST_IP" ]; then HOST_IP="localhost"; fi

  echo ""
  echo -e "${GREEN}${BOLD}======================================================${NC}"
  echo -e "${GREEN}${BOLD}   Resono Full Stack is UP and RUNNING! 🚀            ${NC}"
  echo -e "${GREEN}${BOLD}======================================================${NC}"
  echo ""
  echo -e "  • ${BOLD}Jellyfin Web:${NC}    http://${HOST_IP}:8096  (or http://localhost:8096)"
  echo -e "  • ${BOLD}Resono Gateway:${NC}  http://${HOST_IP}:8080"
  echo ""
  echo -e "${BOLD}Next Steps:${NC}"
  echo -e "  1. Open Jellyfin in your browser and complete the initial user setup."
  echo -e "  2. Add a Music library pointing to ${CYAN}/media/music${NC}."
  echo -e "  3. Go to ${BOLD}Dashboard -> Plugins -> Resono${NC}."
  echo -e "  4. Set Gateway URL to: ${CYAN}http://resono-gateway:8080${NC} and click Save."
  echo -e "  5. Search or play any song from Discrete, Finamp, Manet, or Jellyfin Web!"
  echo ""
else
  echo -e "${CYAN}Setup complete! Whenever you are ready, run:${NC}"
  echo -e "  cd $(pwd)"
  echo -e "  ${COMPOSE_CMD} up -d"
fi