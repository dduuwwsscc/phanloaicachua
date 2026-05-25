/*
  
#include <Arduino.h>
#include <WiFi.h>
#include <ESP32Servo.h>

// ======================= PIN MAP =======================


#define SERVO1_PIN       17
#define SERVO2_PIN       18
#define SERVO3_PIN       16


#define SENSOR1_PIN      10
#define SENSOR2_PIN      12
#define SENSOR3_PIN      11

#define L298_EN_PIN      38
#define L298_IN1_PIN     41
#define L298_IN2_PIN     42

#define LED_STATUS_PIN   48

// ======================= WIFI SERVER CONFIG =======================

const char* WIFI_AP_SSID = "PhanLoaiCaChua";
const char* WIFI_AP_PASS = "88888888";
const uint16_t TCP_SERVER_PORT = 8888;

WiFiServer tcpServer(TCP_SERVER_PORT);
WiFiClient tcpClient;

String tcpRxLine = "";
uint32_t lastTcpRxCharMs = 0;
bool currentCommandFromTcp = false;


// ======================= SERVO CONFIG =======================

Servo servo1;
Servo servo2;
Servo servo3;

const int SERVO_MIN_US = 500;
const int SERVO_MAX_US = 2500;

// Logic servo
const int SERVO_RETRACT_ANGLE = 0;      // thu can
const int SERVO_PUSH_ANGLE    = 180;    // day ra xa nhat

// Neu co khi servo nao lap nguoc, doi false -> true
const bool SERVO1_REVERSE = false;
const bool SERVO2_REVERSE = false;
const bool SERVO3_REVERSE = false;

// ======================= TIME CONFIG =======================

const uint32_t SENSOR_EXTRA_RUN_MS  = 500;    // Sensor thay vat -> bang tai chay them 0.5s
const uint32_t WAIT_BEFORE_PUSH_MS  = 1000;   // Dung bang tai -> doi 1s -> day servo
const uint32_t PUSH_HOLD_MS         = 1500;   // Giu ca 3 servo o 180 do
const uint32_t SERVO_RETURN_WAIT_MS = 300;    // Doi sau khi ve 0 do
const uint32_t ERROR_BELT_RUN_MS    = 21000;   // Lenh error: bang tai chay 7s
const uint32_t SENSOR_TIMEOUT_MS    = 30000;  // Timeout 30s neu khong thay vat

const uint16_t SENSOR_DEBOUNCE_MS   = 30;
const uint32_t SENSOR_LOG_MS        = 500;

// Ho tro Serial Monitor de No line ending
const uint32_t UART_COMMAND_IDLE_MS = 120;

// ======================= SENSOR STATE =======================

struct SensorState {
  uint8_t pin;
  bool rawDetected;
  bool stableDetected;
  uint32_t lastChangeMs;
};

SensorState sensors[3] = {
  {SENSOR1_PIN, false, false, 0},
  {SENSOR2_PIN, false, false, 0},
  {SENSOR3_PIN, false, false, 0}
};

// ======================= MACHINE STATE =======================

enum MachineState {
  ST_IDLE = 0,
  ST_WAIT_SENSOR,
  ST_EXTRA_RUN,
  ST_WAIT_BEFORE_PUSH,
  ST_PUSH_ALL_SERVOS,
  ST_RETURN_ALL_SERVOS,
  ST_ERROR_RUN
};

MachineState state = ST_IDLE;

uint8_t activeSensorId = 0;
String activeCommand = "";

uint32_t stateStartMs = 0;
uint32_t commandStartMs = 0;

bool beltRunning = false;
bool beltForward = true;

int currentServoLogicAngle = 0;

uint32_t lastSensorLogMs = 0;
uint32_t lastLedBlinkMs = 0;
bool ledState = false;

String rxLine = "";
uint32_t lastRxCharMs = 0;


// ======================= WIFI SERVER FUNCTIONS =======================

void sendToPC(const String &msg) {
  if (tcpClient && tcpClient.connected()) {
    tcpClient.print(msg);
    if (!msg.endsWith("\n")) {
      tcpClient.print("\n");
    }
  }
}

void notifyAll(const String &msg) {
  Serial.print("[NOTIFY] ");
  Serial.println(msg);
  sendToPC(msg);
}

void setupWiFiServer() {
  WiFi.mode(WIFI_AP);

  bool ok = WiFi.softAP(WIFI_AP_SSID, WIFI_AP_PASS);
  delay(500);

  IPAddress ip = WiFi.softAPIP();

  tcpServer.begin();
  tcpServer.setNoDelay(true);

  Serial.println();
  Serial.println("========== WIFI SERVER ==========");
  Serial.printf("AP SSID : %s\r\n", WIFI_AP_SSID);
  Serial.printf("AP PASS : %s\r\n", WIFI_AP_PASS);
  Serial.printf("AP IP   : %s\r\n", ip.toString().c_str());
  Serial.printf("TCP PORT: %u\r\n", TCP_SERVER_PORT);
  Serial.printf("WiFi AP : %s\r\n", ok ? "OK" : "FAIL");
  Serial.println("May tinh ket noi WiFi vao AP nay, sau do TCP toi IP tren va port 8888.");
  Serial.println("=================================");
  Serial.println();
}

void checkTcpClient() {
  WiFiClient newClient = tcpServer.available();

  if (newClient) {
    if (tcpClient && tcpClient.connected()) {
      tcpClient.println("DISCONNECTED replaced_by_new_client");
      tcpClient.stop();
    }

    tcpClient = newClient;
    tcpClient.setNoDelay(true);
    tcpRxLine = "";

    IPAddress ip = WiFi.softAPIP();

    Serial.println("[TCP] Client da ket noi.");
    tcpClient.println("CONNECTED PhanLoaiCaChua");
    tcpClient.print("IP ");
    tcpClient.println(ip);
    tcpClient.print("PORT ");
    tcpClient.println(TCP_SERVER_PORT);
    tcpClient.println("READY commands: red/yellow/green/error/push/home/status/sensor/stop");
    
  }

  if (tcpClient && !tcpClient.connected()) {
    Serial.println("[TCP] Client da ngat ket noi.");
    tcpClient.stop();
    tcpRxLine = "";
  }
}

// ======================= BASIC HELPERS =======================

const char* stateName(MachineState st) {
  switch (st) {
    case ST_IDLE:               return "IDLE";
    case ST_WAIT_SENSOR:        return "WAIT_SENSOR";
    case ST_EXTRA_RUN:          return "EXTRA_RUN";
    case ST_WAIT_BEFORE_PUSH:   return "WAIT_BEFORE_PUSH";
    case ST_PUSH_ALL_SERVOS:    return "PUSH_ALL_SERVOS";
    case ST_RETURN_ALL_SERVOS:  return "RETURN_ALL_SERVOS";
    case ST_ERROR_RUN:          return "ERROR_RUN";
    default:                    return "UNKNOWN";
  }
}

String getToken(String data, int index) {
  data.trim();

  int tokenStart = 0;
  int tokenIndex = 0;

  for (int i = 0; i <= data.length(); i++) {
    if (i == data.length() || data[i] == ' ') {
      if (tokenIndex == index) {
        return data.substring(tokenStart, i);
      }

      while (i + 1 < data.length() && data[i + 1] == ' ') {
        i++;
      }

      tokenStart = i + 1;
      tokenIndex++;
    }
  }

  return "";
}

int physicalAngleFromLogic(uint8_t servoId, int logicAngle) {
  logicAngle = constrain(logicAngle, 0, 180);

  bool reverse = false;

  if (servoId == 1) reverse = SERVO1_REVERSE;
  else if (servoId == 2) reverse = SERVO2_REVERSE;
  else if (servoId == 3) reverse = SERVO3_REVERSE;

  return reverse ? (180 - logicAngle) : logicAngle;
}

// ======================= SENSOR FUNCTIONS =======================

bool readSensorDetected(uint8_t pin) {
  return digitalRead(pin) == LOW; // active LOW
}

const char* detectedText(bool detected) {
  return detected ? "CO VAT" : "KHONG";
}

void updateSensors() {
  uint32_t now = millis();

  for (int i = 0; i < 3; i++) {
    bool newRawDetected = readSensorDetected(sensors[i].pin);

    if (newRawDetected != sensors[i].rawDetected) {
      sensors[i].rawDetected = newRawDetected;
      sensors[i].lastChangeMs = now;
    }

    if (now - sensors[i].lastChangeMs >= SENSOR_DEBOUNCE_MS) {
      sensors[i].stableDetected = sensors[i].rawDetected;
    }
  }
}

bool isSensorDetected(uint8_t sensorId) {
  if (sensorId < 1 || sensorId > 3) return false;
  return sensors[sensorId - 1].stableDetected;
}

void printSensorStatus() {
  updateSensors();

  Serial.printf("[SENSOR] "
                "S1 raw=%d => %s | "
                "S2 raw=%d => %s | "
                "S3 raw=%d => %s\r\n",
                digitalRead(SENSOR1_PIN), detectedText(sensors[0].stableDetected),
                digitalRead(SENSOR2_PIN), detectedText(sensors[1].stableDetected),
                digitalRead(SENSOR3_PIN), detectedText(sensors[2].stableDetected));
}

// ======================= LED FUNCTIONS =======================

void updateLed() {
  uint32_t now = millis();
  uint32_t blinkInterval = (state == ST_IDLE) ? 700 : 150;

  if (now - lastLedBlinkMs >= blinkInterval) {
    lastLedBlinkMs = now;
    ledState = !ledState;
    digitalWrite(LED_STATUS_PIN, ledState ? HIGH : LOW);
  }
}

// ======================= BELT FUNCTIONS =======================

void applyBelt() {
  if (!beltRunning) {
    digitalWrite(L298_EN_PIN, LOW);
    digitalWrite(L298_IN1_PIN, LOW);
    digitalWrite(L298_IN2_PIN, LOW);
    return;
  }

  if (beltForward) {
    digitalWrite(L298_IN1_PIN, HIGH);
    digitalWrite(L298_IN2_PIN, LOW);
  } else {
    digitalWrite(L298_IN1_PIN, LOW);
    digitalWrite(L298_IN2_PIN, HIGH);
  }

  digitalWrite(L298_EN_PIN, HIGH);
}

void beltOn() {
  beltRunning = true;
  applyBelt();
  Serial.println("[BELT] ON");
}

void beltOff() {
  beltRunning = false;
  applyBelt();
  Serial.println("[BELT] OFF");
}

void beltSetForward(bool forward) {
  beltForward = forward;
  applyBelt();
  Serial.printf("[BELT] Direction = %s\r\n", beltForward ? "THUAN" : "NGUOC");
}

// ======================= SERVO FUNCTIONS =======================

void allServosWriteLogic(int logicAngle) {
  logicAngle = constrain(logicAngle, 0, 180);

  int a1 = physicalAngleFromLogic(1, logicAngle);
  int a2 = physicalAngleFromLogic(2, logicAngle);
  int a3 = physicalAngleFromLogic(3, logicAngle);

  servo1.write(a1);
  servo2.write(a2);
  servo3.write(a3);

  currentServoLogicAngle = logicAngle;

  Serial.printf("[SERVO] ALL -> logic=%d do | S1 physical=%d | S2 physical=%d | S3 physical=%d\r\n",
                logicAngle, a1, a2, a3);
}

void allServoHome() {
  Serial.println("[HOME] Thu ca 3 servo ve logic 0 do.");
  allServosWriteLogic(SERVO_RETRACT_ANGLE);
}

void pushAllServosOnce() {
  Serial.println("[TEST] Ca 3 servo cung day ra roi thu ve.");

  allServosWriteLogic(SERVO_RETRACT_ANGLE);
  delay(500);

  allServosWriteLogic(SERVO_PUSH_ANGLE);
  delay(PUSH_HOLD_MS);

  allServosWriteLogic(SERVO_RETRACT_ANGLE);
  delay(500);

  notifyAll("DONE push");
}

// ======================= STATE MACHINE =======================

void enterState(MachineState newState) {
  state = newState;
  stateStartMs = millis();
  Serial.printf("[STATE] -> %s\r\n", stateName(state));
}

void startSortCommand(uint8_t sensorId, const char* commandName) {
  if (state != ST_IDLE) {
    Serial.printf("[BUSY] Dang xu ly lenh truoc, state=%s. Gui stop neu muon huy.\r\n", stateName(state));
    return;
  }

  activeSensorId = sensorId;
  activeCommand = commandName;
  commandStartMs = millis();

  Serial.println();
  Serial.printf("[CMD] %s: bang tai chay den Sensor%d, sau do CA 3 SERVO cung day.\r\n",
                commandName, activeSensorId);

  allServosWriteLogic(SERVO_RETRACT_ANGLE);

  beltSetForward(true);
  beltOn();

  enterState(ST_WAIT_SENSOR);
  notifyAll(String("START ") + commandName + " sensor=" + String(activeSensorId));
}

void startErrorCommand() {
  if (state != ST_IDLE) {
    Serial.printf("[BUSY] Dang xu ly lenh truoc, state=%s. Gui stop neu muon huy.\r\n", stateName(state));
    return;
  }

  activeSensorId = 0;
  activeCommand = "error";
  commandStartMs = millis();

  Serial.println();
  Serial.printf("[CMD] error: bang tai chay %lu ms roi dung, khong day servo.\r\n",
                (unsigned long)ERROR_BELT_RUN_MS);

  beltSetForward(true);
  beltOn();

  enterState(ST_ERROR_RUN);
  notifyAll("START error");
}

void stopAll() {
  Serial.println("[STOP] Dung khan cap.");

  beltOff();
  allServoHome();

  activeSensorId = 0;
  activeCommand = "";
  enterState(ST_IDLE);
  notifyAll("STOPPED");
}

void updateMachine() {
  uint32_t now = millis();

  switch (state) {
    case ST_IDLE:
      break;

    case ST_WAIT_SENSOR:
      if (isSensorDetected(activeSensorId)) {
        Serial.printf("[DETECT] Sensor%d da phat hien vat. Bang tai chay them %lu ms.\r\n",
                      activeSensorId, (unsigned long)SENSOR_EXTRA_RUN_MS);
        enterState(ST_EXTRA_RUN);
      }

      if (SENSOR_TIMEOUT_MS > 0 && now - commandStartMs >= SENSOR_TIMEOUT_MS) {
        Serial.printf("[TIMEOUT] Qua %lu ms chua thay vat o Sensor%d. Dung he thong.\r\n",
                      (unsigned long)SENSOR_TIMEOUT_MS, activeSensorId);
        notifyAll(String("TIMEOUT ") + activeCommand + " sensor=" + String(activeSensorId));
        stopAll();
      }
      break;

    case ST_EXTRA_RUN:
      if (now - stateStartMs >= SENSOR_EXTRA_RUN_MS) {
        beltOff();
        Serial.printf("[WAIT] Doi %lu ms truoc khi day ca 3 servo.\r\n",
                      (unsigned long)WAIT_BEFORE_PUSH_MS);
        enterState(ST_WAIT_BEFORE_PUSH);
      }
      break;

    case ST_WAIT_BEFORE_PUSH:
      if (now - stateStartMs >= WAIT_BEFORE_PUSH_MS) {
        Serial.printf("[PUSH] Ca 3 servo cung day ra %d do.\r\n", SERVO_PUSH_ANGLE);
        allServosWriteLogic(SERVO_PUSH_ANGLE);
        enterState(ST_PUSH_ALL_SERVOS);
      }
      break;

    case ST_PUSH_ALL_SERVOS:
      if (now - stateStartMs >= PUSH_HOLD_MS) {
        Serial.printf("[RETURN] Ca 3 servo cung thu ve %d do.\r\n", SERVO_RETRACT_ANGLE);
        allServosWriteLogic(SERVO_RETRACT_ANGLE);
        enterState(ST_RETURN_ALL_SERVOS);
      }
      break;

    case ST_RETURN_ALL_SERVOS:
      if (now - stateStartMs >= SERVO_RETURN_WAIT_MS) {
        Serial.println("[DONE] Hoan thanh chu trinh.");
        notifyAll(String("DONE ") + (activeCommand.length() ? activeCommand : "sort"));
        activeSensorId = 0;
        activeCommand = "";
        enterState(ST_IDLE);
      }
      break;

    case ST_ERROR_RUN:
      if (now - stateStartMs >= ERROR_BELT_RUN_MS) {
        beltOff();
        Serial.println("[DONE] Lenh error hoan thanh.");
        notifyAll("DONE error");
        activeCommand = "";
        enterState(ST_IDLE);
      }
      break;
  }
}

// ======================= STATUS / HELP =======================

void printStatus() {
  Serial.println();
  Serial.println("========== STATUS ==========");
  Serial.printf("State: %s\r\n", stateName(state));
  Serial.printf("Active command: %s | Active sensor: %d\r\n",
                activeCommand.length() ? activeCommand.c_str() : "none",
                activeSensorId);
  Serial.printf("Belt: %s | Direction: %s\r\n",
                beltRunning ? "ON" : "OFF",
                beltForward ? "THUAN" : "NGUOC");

  Serial.printf("Servo pins: S1=GPIO%d, S2=GPIO%d, S3=GPIO%d\r\n",
                SERVO1_PIN, SERVO2_PIN, SERVO3_PIN);
  Serial.printf("Servo logic angle current: %d\r\n", currentServoLogicAngle);
  Serial.printf("Servo logic: 0=thu can, 180=day ra\r\n");

  Serial.printf("Sensor pins: S1=GPIO%d, S2=GPIO%d, S3=GPIO%d, active LOW\r\n",
                SENSOR1_PIN, SENSOR2_PIN, SENSOR3_PIN);

  Serial.printf("Times: extraRun=%lums, waitBeforePush=%lums, pushHold=%lums, errorRun=%lums, timeout=%lums\r\n",
                (unsigned long)SENSOR_EXTRA_RUN_MS,
                (unsigned long)WAIT_BEFORE_PUSH_MS,
                (unsigned long)PUSH_HOLD_MS,
                (unsigned long)ERROR_BELT_RUN_MS,
                (unsigned long)SENSOR_TIMEOUT_MS);

  printSensorStatus();
  Serial.println("============================");
  Serial.println();
}

void printHelp() {
  Serial.println();
  Serial.println("========== LENH UART ==========");
  Serial.println("red        : bang tai -> Sensor1 -> ca 3 servo day");
  Serial.println("yellow     : bang tai -> Sensor2 -> ca 3 servo day");
  Serial.println("green      : bang tai -> Sensor3 -> ca 3 servo day");
  Serial.println("error      : bang tai chay 7s roi dung");
  Serial.println();
  Serial.println("push       : test ca 3 servo day 180 roi thu ve 0");
  Serial.println("home       : ca 3 servo ve 0 do");
  Serial.println("all 0      : ca 3 servo ve 0 do");
  Serial.println("all 180    : ca 3 servo ve 180 do");
  Serial.println();
  Serial.println("sensor     : doc 3 cam bien");
  Serial.println("status     : hien trang thai");
  Serial.println("stop       : dung khan cap");
  Serial.println();
  Serial.println("belt on    : bat bang tai thu cong");
  Serial.println("belt off   : tat bang tai thu cong");
  Serial.println("belt fwd   : chieu thuan");
  Serial.println("belt rev   : chieu nguoc");
  Serial.println("===============================");
  Serial.println();
}

// ======================= UART PARSER =======================

void handleCommand(String cmd) {
  cmd.trim();
  cmd.toLowerCase();

  if (cmd.length() == 0) return;

  Serial.print(currentCommandFromTcp ? "[RX-TCP] " : "[RX-UART] ");
  Serial.println(cmd);

  if (currentCommandFromTcp) {
    sendToPC(String("ACK ") + cmd);
  }

  String t0 = getToken(cmd, 0);
  String t1 = getToken(cmd, 1);

  if (t0 == "help" || t0 == "?") {
    printHelp();
  }
  else if (t0 == "red") {
    startSortCommand(1, "red");
  }
  else if (t0 == "yellow") {
    startSortCommand(2, "yellow");
  }
  else if (t0 == "green") {
    startSortCommand(3, "green");
  }
  else if (t0 == "error") {
    startErrorCommand();
  }
  else if (t0 == "push") {
    pushAllServosOnce();
  }
  else if (t0 == "home") {
    allServoHome();
  }
  else if (t0 == "all") {
    int angle = t1.toInt();
    allServosWriteLogic(angle);
  }
  else if (t0 == "stop") {
    stopAll();
  }
  else if (t0 == "status") {
    printStatus();
  }
  else if (t0 == "sensor") {
    printSensorStatus();
  }
  else if (t0 == "belt") {
    if (t1 == "on") {
      beltOn();
    } else if (t1 == "off" || t1 == "stop") {
      beltOff();
    } else if (t1 == "fwd") {
      beltSetForward(true);
    } else if (t1 == "rev") {
      beltSetForward(false);
    } else {
      Serial.println("[ERR] Dung: belt on / belt off / belt fwd / belt rev");
    }
  }
  else {
    Serial.print("[ERR] Khong hieu lenh: ");
    Serial.println(cmd);
    Serial.println("Gui help de xem lenh.");
    if (currentCommandFromTcp) sendToPC(String("ERR unknown_command ") + cmd);
  }
}

void processRxLineIfAny() {
  rxLine.trim();

  if (rxLine.length() > 0) {
    handleCommand(rxLine);
  }

  rxLine = "";
}

void readSerialCommand() {
  while (Serial.available()) {
    char c = (char)Serial.read();
    lastRxCharMs = millis();

    if (c == '\n' || c == '\r' || c == ';') {
      processRxLineIfAny();
    } else {
      rxLine += c;

      if (rxLine.length() > 100) {
        rxLine = "";
        Serial.println("[ERR] Lenh qua dai, xoa buffer.");
      }
    }
  }

  // Neu Serial Monitor de No line ending, sau 120ms se tu xu ly lenh
  if (rxLine.length() > 0 && (millis() - lastRxCharMs >= UART_COMMAND_IDLE_MS)) {
    processRxLineIfAny();
  }
}

void processTcpRxLineIfAny() {
  tcpRxLine.trim();

  if (tcpRxLine.length() > 0) {
    currentCommandFromTcp = true;
    handleCommand(tcpRxLine);
    currentCommandFromTcp = false;
  }

  tcpRxLine = "";
}

void readTcpCommand() {
  checkTcpClient();

  if (!(tcpClient && tcpClient.connected())) {
    return;
  }

  while (tcpClient.available()) {
    char c = (char)tcpClient.read();
    lastTcpRxCharMs = millis();

    if (c == '\n' || c == '\r' || c == ';') {
      processTcpRxLineIfAny();
    } else {
      tcpRxLine += c;

      if (tcpRxLine.length() > 100) {
        tcpRxLine = "";
        sendToPC("ERR command_too_long");
        Serial.println("[TCP] Lenh qua dai, xoa buffer.");
      }
    }
  }

  // Ho tro app gui lenh khong co ky tu ket thuc dong.
  if (tcpRxLine.length() > 0 && (millis() - lastTcpRxCharMs >= UART_COMMAND_IDLE_MS)) {
    processTcpRxLineIfAny();
  }
}

// ======================= SETUP / LOOP =======================

void setup() {
  Serial.begin(115200);
  delay(1000);

  Serial.println();
  Serial.println("=======================================================");
  Serial.println(" ESP32-S3 TOMATO SORTER - WIFI SERVER + ALL 3 SERVOS");
  Serial.println("=======================================================");

  setupWiFiServer();

  pinMode(LED_STATUS_PIN, OUTPUT);
  digitalWrite(LED_STATUS_PIN, LOW);

  pinMode(SENSOR1_PIN, INPUT_PULLUP);
  pinMode(SENSOR2_PIN, INPUT_PULLUP);
  pinMode(SENSOR3_PIN, INPUT_PULLUP);

  for (int i = 0; i < 3; i++) {
    sensors[i].rawDetected = readSensorDetected(sensors[i].pin);
    sensors[i].stableDetected = sensors[i].rawDetected;
    sensors[i].lastChangeMs = millis();
  }

  pinMode(L298_EN_PIN, OUTPUT);
  pinMode(L298_IN1_PIN, OUTPUT);
  pinMode(L298_IN2_PIN, OUTPUT);
  beltOff();

  ESP32PWM::allocateTimer(0);
  ESP32PWM::allocateTimer(1);
  ESP32PWM::allocateTimer(2);
  ESP32PWM::allocateTimer(3);

  servo1.setPeriodHertz(50);
  servo2.setPeriodHertz(50);
  servo3.setPeriodHertz(50);

  int r1 = servo1.attach(SERVO1_PIN, SERVO_MIN_US, SERVO_MAX_US);
  int r2 = servo2.attach(SERVO2_PIN, SERVO_MIN_US, SERVO_MAX_US);
  int r3 = servo3.attach(SERVO3_PIN, SERVO_MIN_US, SERVO_MAX_US);

  Serial.printf("[ATTACH] Servo1 GPIO%d result/channel = %d\r\n", SERVO1_PIN, r1);
  Serial.printf("[ATTACH] Servo2 GPIO%d result/channel = %d\r\n", SERVO2_PIN, r2);
  Serial.printf("[ATTACH] Servo3 GPIO%d result/channel = %d\r\n", SERVO3_PIN, r3);

  allServoHome();
  delay(1500);

  printHelp();
  printStatus();

  Serial.println("[READY] San sang nhan lenh: red / yellow / green / error");
}

void loop() {
  updateSensors();
  readSerialCommand();
  readTcpCommand();
  updateMachine();
  updateLed();

  if (millis() - lastSensorLogMs >= SENSOR_LOG_MS) {
    lastSensorLogMs = millis();

    if (state != ST_IDLE) {
      printSensorStatus();
    }
  }
}
